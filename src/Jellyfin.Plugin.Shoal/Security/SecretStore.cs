using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.State;
using Jellyfin.Plugin.Shoal.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Shoal.Security;

/// <summary>
/// The encrypted store for peer tokens (<c>secrets.enc</c>, DESIGN 8.2).
/// <para>
/// <b>Key.</b> From <c>JELLYFIN_SHOAL_KEY</c> or from the file named by <c>JELLYFIN_SHOAL_KEY_FILE</c> (exactly 32 random bytes as
/// base64url or hex, see <see cref="SecretKeyParser"/>; both set is refused; a bad value is refused and never falls back to
/// <c>master.key</c>, never generates a key and never touches <c>master.key</c>). Otherwise <c>master.key</c> in the data folder:
/// wrapped with DPAPI (<c>CurrentUser</c>, with application entropy) on Windows, 32 raw bytes with mode 0600 elsewhere. A missing
/// <c>master.key</c> next to an existing <c>secrets.enc</c> is never repaired by making a new key: that would orphan the secrets.
/// </para>
/// <para>
/// <b>Construction.</b> AES key = HKDF-SHA256(master key, no salt, info <c>shoal-secrets-v1</c>). One AES-256-GCM message per peer,
/// each with a fresh random 96-bit nonce at every save. The additional data binds the entry to its peer key and to the peer's host
/// (SHA-256 of the lower-case punycode host), so an attacker who can edit <c>state.json</c> but cannot read the key cannot point a
/// stored token at another host or peer. A <c>check</c> message under the same key tells a wrong key (the whole store is refused)
/// from one changed or re-pointed entry (that peer is refused, the rest keep working). Key rotation is out of scope: a new key means
/// re-pairing.
/// </para>
/// The constructor never throws and never writes.
/// </summary>
public sealed class SecretStore : ISecretStore, IDisposable
{
    /// <summary>The environment variable that holds the key.</summary>
    public const string KeyVariable = "JELLYFIN_SHOAL_KEY";

    /// <summary>The environment variable that names a file that holds the key.</summary>
    public const string KeyFileVariable = "JELLYFIN_SHOAL_KEY_FILE";

    private const int FileSchema = 1;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 1000;
    private const int MaxKeyFileBytes = 1024;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxSealedBytes = 4096;
    private const string HkdfInfo = "shoal-secrets-v1";
    private static readonly byte[] DpapiMagic = Encoding.ASCII.GetBytes("SHOALKEY-DPAPI1\n");
    private static readonly byte[] CheckPlaintext = Encoding.ASCII.GetBytes("shoal-secrets-check");
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ShoalPaths _paths;
    private readonly IStateStore _state;
    private readonly ILogger _logger;
    private readonly Func<string, string?> _environment;
    private readonly IKeyProtector? _protector;
    private readonly Action<AtomicStage>? _atomicHook;
    private readonly string? _mountInfo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();

    private Dictionary<string, Entry> _entries = [];
    private byte[]? _aesKey;
    private SecretStoreStatus _status = SecretStoreStatus.Unreadable;
    private SecretUnreadableReason _reason = SecretUnreadableReason.StorageUnavailable;
    private KeySource _keySource;
    private string? _volumeWarning;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin's application paths; the data lives in <c>DataPath/shoal</c>.</param>
    /// <param name="state">The state store (the peer's host is part of what a sealed entry is bound to).</param>
    /// <param name="logger">The logger.</param>
    public SecretStore(IApplicationPaths applicationPaths, IStateStore state, ILogger<SecretStore> logger)
        : this(new ShoalPaths(applicationPaths.DataPath), state, logger, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows() ? CreateDpapi() : null, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretStore"/> class (tests: own folder, own environment, own key wrapper).
    /// </summary>
    /// <param name="paths">The data folder layout.</param>
    /// <param name="state">The state store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="environment">Reads an environment variable (null when not set).</param>
    /// <param name="protector">Wraps <c>master.key</c> (Windows DPAPI); null stores the key as 32 raw bytes with mode 0600.</param>
    /// <param name="atomicHook">Test seam to simulate a crash inside a write.</param>
    /// <param name="mountInfo">Test seam: the content of <c>/proc/self/mountinfo</c>.</param>
    internal SecretStore(ShoalPaths paths, IStateStore state, ILogger logger, Func<string, string?> environment, IKeyProtector? protector, Action<AtomicStage>? atomicHook, string? mountInfo)
    {
        _paths = paths;
        _state = state;
        _logger = logger;
        _environment = environment;
        _protector = protector;
        _atomicHook = atomicHook;
        _mountInfo = mountInfo;
        Load();
    }

    /// <inheritdoc />
    public SecretStoreStatus Status => _status;

    /// <inheritdoc />
    public SecretStoreInfo Info
    {
        get
        {
            lock (_lock)
            {
                var ready = _status == SecretStoreStatus.Ready;
                return new SecretStoreInfo
                {
                    Status = _status,
                    Reason = ready ? SecretUnreadableReason.None : _reason,
                    KeySource = _keySource,
                    Entries = ready ? _entries.Count : 0,
                    VolumeWarning = _volumeWarning,
                    RefusedPeerKeys = ready ? _entries.Keys.Where(IsRefusedLocked).Order(StringComparer.Ordinal).ToList() : [],
                    Hint = ready ? null : HintFor(_reason)
                };
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string>? PeerKeys
    {
        get
        {
            lock (_lock)
            {
                return _status == SecretStoreStatus.Ready ? _entries.Keys.Order(StringComparer.Ordinal).ToList() : null;
            }
        }
    }

    /// <inheritdoc />
    public PeerCredential? TryGet(string peerKey)
    {
        if (_status != SecretStoreStatus.Ready || peerKey is null)
        {
            return null;
        }

        lock (_lock)
        {
            return _entries.TryGetValue(peerKey, out var entry) ? ResolveLocked(peerKey, entry) : null;
        }
    }

    /// <inheritdoc />
    public async Task PutAsync(string peerKey, PeerCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!ShoalPaths.IsValidPeerKey(peerKey))
        {
            throw new ArgumentException("Invalid peer key.", nameof(peerKey));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireReady();
            var host = HostHashOf(peerKey) ?? throw new InvalidOperationException("The peer is not in the state, or its address is not valid; the credential was not stored.");
            EnsureKey();
            var next = SnapshotForWrite();
            next[peerKey] = new Entry(Seal(Encode(credential), peerKey, host), credential, host);
            WriteAll(next);
            _logger.LogInformation("Shoal stored a credential for peer {Peer} ({Method})", Redact.Id(peerKey), credential.Method);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string peerKey, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireReady();
            bool exists;
            lock (_lock)
            {
                exists = peerKey is not null && _entries.ContainsKey(peerKey);
            }

            if (!exists)
            {
                return false;
            }

            var next = SnapshotForWrite();
            next.Remove(peerKey!);
            WriteAll(next);
            _logger.LogInformation("Shoal removed the credential of peer {Peer}", Redact.Id(peerKey));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keySource is KeySource.Environment or KeySource.EnvironmentFile || _reason is SecretUnreadableReason.BadKey or SecretUnreadableReason.AmbiguousKey)
            {
                throw new InvalidOperationException("The key comes from the environment: fix JELLYFIN_SHOAL_KEY or JELLYFIN_SHOAL_KEY_FILE first.");
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            foreach (var path in new[] { _paths.SecretsFile, _paths.MasterKeyFile })
            {
                if (File.Exists(path))
                {
                    File.Move(path, path + ".corrupt-" + stamp);
                }
            }

            Load();
            _logger.LogWarning("Shoal started its secret store over; the previous files were kept as *.corrupt-*");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        lock (_lock)
        {
            ClearKeyLocked();
        }
    }

    private static string? HintFor(SecretUnreadableReason reason) => reason switch
    {
        SecretUnreadableReason.BadKey => SecretKeyParser.HowToMakeAKey,
        SecretUnreadableReason.AmbiguousKey => "Set only one of JELLYFIN_SHOAL_KEY and JELLYFIN_SHOAL_KEY_FILE.",
        SecretUnreadableReason.MasterKeyMissing => "master.key is missing next to secrets.enc: restore it from a backup, or use Start over and pair your friends again.",
        SecretUnreadableReason.Tampered => "secrets.enc does not match the key: restore the key, or use Start over and pair your friends again.",
        SecretUnreadableReason.KeyProtectionFailed => "master.key was wrapped for another Windows account or machine: run Jellyfin as the account that created it, or use Start over and pair again.",
        SecretUnreadableReason.CryptoUnavailable => "This platform has no AES-GCM support; use an image with OpenSSL.",
        _ => null
    };

    private static DpapiKeyProtector? CreateDpapi() => OperatingSystem.IsWindows() ? new DpapiKeyProtector() : null;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            MaxDepth = 8,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            AllowDuplicateProperties = false,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private static string? HostHashFromUrl(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.IdnHost))
        {
            return null;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.IdnHost.ToLowerInvariant())));
    }

    private static string Aad(string peerKey, string hostHash) =>
        string.Create(CultureInfo.InvariantCulture, $"shoal-secrets|schema={FileSchema}|peer={peerKey}|host={hostHash}");

    private static byte[] CheckAad() => Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"shoal-secrets|schema={FileSchema}|check"));

    private static byte[] Encode(PeerCredential credential) => JsonSerializer.SerializeToUtf8Bytes(
        new Plain { Token = credential.Token, DeviceId = credential.DeviceId, PairedUtc = credential.PairedUtc, Method = credential.Method },
        JsonOptions);

    private static PeerCredential? Decode(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var plain = JsonSerializer.Deserialize<Plain>(utf8, JsonOptions);
            return plain is null ? null : new PeerCredential(plain.Token, plain.DeviceId, DateTime.SpecifyKind(plain.PairedUtc, DateTimeKind.Utc), plain.Method);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] DeriveAesKey(byte[] masterKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, salt: [], info: Encoding.ASCII.GetBytes(HkdfInfo));

    private static bool TryDecodeBox(SealedBox? box, out byte[] nonce, out byte[] ciphertext)
    {
        nonce = [];
        ciphertext = [];
        if (box?.Nonce is null || box.Ciphertext is null || box.Nonce.Length > 32 || box.Ciphertext.Length > MaxSealedBytes * 2)
        {
            return false;
        }

        try
        {
            nonce = System.Buffers.Text.Base64Url.DecodeFromChars(box.Nonce);
            ciphertext = System.Buffers.Text.Base64Url.DecodeFromChars(box.Ciphertext);
        }
        catch (FormatException)
        {
            return false;
        }

        return nonce.Length == NonceBytes && ciphertext.Length is > TagBytes and <= MaxSealedBytes;
    }

    private void Load()
    {
        lock (_lock)
        {
            _entries = [];
            ClearKeyLocked();
            _status = SecretStoreStatus.Unreadable;
            _reason = SecretUnreadableReason.StorageUnavailable;
            _keySource = KeySource.None;
            _volumeWarning = null;
        }

        try
        {
            if (!AesGcm.IsSupported)
            {
                SetUnreadable(SecretUnreadableReason.CryptoUnavailable);
                return;
            }

            _volumeWarning = VolumeCheck.CheckFileSystem(_paths.Root, _mountInfo);
            if (!ResolveKey(out var reason))
            {
                SetUnreadable(reason);
                return;
            }

            if (!File.Exists(_paths.SecretsFile))
            {
                lock (_lock)
                {
                    _status = SecretStoreStatus.Ready;
                    _reason = SecretUnreadableReason.None;
                }

                return;
            }

            if (_aesKey is null)
            {
                SetUnreadable(SecretUnreadableReason.MasterKeyMissing);
                return;
            }

            LoadFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetUnreadable(SecretUnreadableReason.StorageUnavailable);
        }
#pragma warning disable CA1031 // Fail closed: whatever the platform throws (no GCM, odd paths), the store is unreadable and the server still starts.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError("Shoal secret store failed to start: {Error}", ex.GetType().Name);
            SetUnreadable(SecretUnreadableReason.StorageUnavailable);
        }

        if (_status == SecretStoreStatus.Ready)
        {
            _volumeWarning ??= VolumeCheck.CheckFileMode(_paths.SecretsFile);
        }
    }

    private void SetUnreadable(SecretUnreadableReason reason)
    {
        lock (_lock)
        {
            _status = SecretStoreStatus.Unreadable;
            _reason = reason;
            _entries = [];
        }

        _logger.LogError("Shoal cannot read its secrets ({Reason}); every peer is refused. The key is never regenerated.", reason);
    }

    // Finds the master key. Returns false with the reason when it cannot. A missing key with no secrets.enc is fine: it is created on the
    // first Put. With a key from the environment master.key is neither read nor written.
    private bool ResolveKey(out SecretUnreadableReason reason)
    {
        reason = SecretUnreadableReason.None;
        var fromVariable = _environment(KeyVariable);
        var fromFile = _environment(KeyFileVariable);
        if (fromVariable is not null && fromFile is not null)
        {
            reason = SecretUnreadableReason.AmbiguousKey;
            return false;
        }

        if (fromVariable is not null || fromFile is not null)
        {
            var text = fromVariable ?? ReadKeyFile(fromFile!);
            if (text is null || !SecretKeyParser.TryParse(text, out var master))
            {
                reason = SecretUnreadableReason.BadKey;
                return false;
            }

            using (new Zeroing(master))
            {
                lock (_lock)
                {
                    _aesKey = DeriveAesKey(master);
                    _keySource = fromVariable is not null ? KeySource.Environment : KeySource.EnvironmentFile;
                }
            }

            return true;
        }

        if (!File.Exists(_paths.MasterKeyFile))
        {
            return true; // nothing to load; see the caller
        }

        var keySource = _protector is not null ? KeySource.Dpapi : KeySource.KeyFile;
        var stored = ReadMasterKeyFile(out reason);
        if (stored is null)
        {
            return false;
        }

        using (new Zeroing(stored))
        {
            lock (_lock)
            {
                _aesKey = DeriveAesKey(stored);
                _keySource = keySource;
            }
        }

        return true;
    }

    private static string? ReadKeyFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length is 0 or > MaxKeyFileBytes)
            {
                return null;
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
            return text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private byte[]? ReadMasterKeyFile(out SecretUnreadableReason reason)
    {
        reason = SecretUnreadableReason.None;
        var bytes = File.ReadAllBytes(_paths.MasterKeyFile);
        if (_protector is not null)
        {
            if (bytes.Length <= DpapiMagic.Length || !bytes.AsSpan(0, DpapiMagic.Length).SequenceEqual(DpapiMagic))
            {
                reason = SecretUnreadableReason.KeyProtectionFailed;
                return null;
            }

            try
            {
                var key = _protector.Unprotect(bytes[DpapiMagic.Length..]);
                if (key.Length == SecretKeyParser.KeyBytes)
                {
                    return key;
                }

                CryptographicOperations.ZeroMemory(key);
                reason = SecretUnreadableReason.Corrupt;
                return null;
            }
            catch (CryptographicException)
            {
                reason = SecretUnreadableReason.KeyProtectionFailed;
                return null;
            }
        }

        if (bytes.Length != SecretKeyParser.KeyBytes)
        {
            reason = SecretUnreadableReason.Corrupt;
            return null;
        }

        return bytes;
    }

    private void LoadFile()
    {
        var info = new FileInfo(_paths.SecretsFile);
        if (info.Length is 0 or > MaxFileBytes)
        {
            SetUnreadable(SecretUnreadableReason.Corrupt);
            return;
        }

        SecretsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SecretsDocument>(File.ReadAllBytes(_paths.SecretsFile), JsonOptions);
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null || document.Schema != FileSchema || document.Check is null || document.Peers is null
            || document.Peers.Count > MaxEntries || document.Peers.Any(p => !ShoalPaths.IsValidPeerKey(p.Key) || p.Value is null))
        {
            SetUnreadable(SecretUnreadableReason.Corrupt);
            return;
        }

        if (!TryDecodeBox(document.Check, out var checkNonce, out var checkCipher) || !TryOpen(checkNonce, checkCipher, CheckAad(), out var checkPlain)
            || !CryptographicOperations.FixedTimeEquals(checkPlain, CheckPlaintext))
        {
            SetUnreadable(SecretUnreadableReason.Tampered);
            return;
        }

        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (peerKey, box) in document.Peers)
        {
            if (!TryDecodeBox(box, out _, out _))
            {
                SetUnreadable(SecretUnreadableReason.Corrupt);
                return;
            }

            entries[peerKey] = new Entry(box, null, null);
        }

        lock (_lock)
        {
            _entries = entries;
            _status = SecretStoreStatus.Ready;
            _reason = SecretUnreadableReason.None;
            foreach (var (peerKey, entry) in _entries)
            {
                _ = ResolveLocked(peerKey, entry); // opens what can be opened now; marks the rest as refused
            }
        }

        var refused = Info.RefusedPeerKeys;
        if (refused.Count > 0)
        {
            _logger.LogError("Shoal refuses {Count} peer credential(s) that do not authenticate (changed entry or changed peer address): {Peers}", refused.Count, string.Join(", ", refused.Select(Redact.Id)));
        }
    }

    // Opens an entry under the peer's current host. A credential that was opened under another host than the state names now stays
    // closed (the entry cannot have been re-sealed without a Put). Must run under _lock.
    private PeerCredential? ResolveLocked(string peerKey, Entry entry)
    {
        var host = HostHashOf(peerKey);
        if (host is null)
        {
            return null; // not in the state (an orphan, reported by the reconciliation) or no valid address
        }

        if (entry.Credential is not null)
        {
            return string.Equals(entry.OpenedUnderHost, host, StringComparison.Ordinal) ? entry.Credential : null;
        }

        if ((entry.Failed && string.Equals(entry.FailedUnderHost, host, StringComparison.Ordinal)) || _aesKey is null || !TryDecodeBox(entry.Sealed, out var nonce, out var cipher))
        {
            return null;
        }

        if (TryOpen(nonce, cipher, Encoding.ASCII.GetBytes(Aad(peerKey, host)), out var plain))
        {
            var credential = Decode(plain);
            CryptographicOperations.ZeroMemory(plain);
            if (credential is not null)
            {
                entry.Credential = credential;
                entry.OpenedUnderHost = host;
                return credential;
            }
        }

        entry.Failed = true;
        entry.FailedUnderHost = host;
        return null;
    }

    private bool IsRefusedLocked(string peerKey)
    {
        var entry = _entries[peerKey];
        var host = HostHashOf(peerKey);
        if (host is null)
        {
            return false;
        }

        if (entry.Credential is not null)
        {
            return !string.Equals(entry.OpenedUnderHost, host, StringComparison.Ordinal);
        }

        _ = ResolveLocked(peerKey, entry);
        return entry.Credential is null;
    }

    private string? HostHashOf(string peerKey)
    {
        var peer = _state.Current.Home.Peers.FirstOrDefault(p => string.Equals(p.PeerKey, peerKey, StringComparison.Ordinal));
        return peer is null ? null : HostHashFromUrl(peer.BaseUrl);
    }

    private bool TryOpen(byte[] nonce, byte[] cipherAndTag, byte[] aad, out byte[] plain)
    {
        plain = [];
        if (_aesKey is null)
        {
            return false;
        }

        try
        {
            using var aes = new AesGcm(_aesKey, TagBytes);
            var text = new byte[cipherAndTag.Length - TagBytes];
            aes.Decrypt(nonce, cipherAndTag.AsSpan(0, text.Length), cipherAndTag.AsSpan(text.Length), text, aad);
            plain = text;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private SealedBox Seal(byte[] plain, string peerKey, string hostHash) => SealWith(plain, Encoding.ASCII.GetBytes(Aad(peerKey, hostHash)));

    private SealedBox SealWith(byte[] plain, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(_aesKey!, TagBytes))
        {
            aes.Encrypt(nonce, plain, cipher, tag, aad);
        }

        CryptographicOperations.ZeroMemory(plain);
        return new SealedBox
        {
            Nonce = System.Buffers.Text.Base64Url.EncodeToString(nonce),
            Ciphertext = System.Buffers.Text.Base64Url.EncodeToString([.. cipher, .. tag])
        };
    }

    private void RequireReady()
    {
        if (_status != SecretStoreStatus.Ready)
        {
            throw new InvalidOperationException("The secret store is unreadable (" + _reason + "); nothing was changed.");
        }
    }

    // Creates master.key on the first Put when no key exists yet. Never when secrets.enc exists (Load refuses that case first) and never
    // when the key comes from the environment (then _aesKey is already set).
    private void EnsureKey()
    {
        if (_aesKey is not null)
        {
            return;
        }

        if (File.Exists(_paths.MasterKeyFile) || File.Exists(_paths.SecretsFile))
        {
            throw new InvalidOperationException("A key or secrets file appeared that this store did not load; restart to load it. Nothing was changed.");
        }

        SecureDirectory.Ensure(_paths.Root);
        var master = RandomNumberGenerator.GetBytes(SecretKeyParser.KeyBytes);
        using (new Zeroing(master))
        {
            byte[] content;
            if (_protector is not null)
            {
                content = [.. DpapiMagic, .. _protector.Protect(master)];
            }
            else
            {
                content = master;
            }

            AtomicFile.Write(_paths.MasterKeyFile, content, backupPath: null, _atomicHook);
            lock (_lock)
            {
                _aesKey = DeriveAesKey(master);
                _keySource = _protector is not null ? KeySource.Dpapi : KeySource.KeyFile;
            }
        }

        _volumeWarning ??= VolumeCheck.CheckFileMode(_paths.MasterKeyFile);
    }

    // The next generation of the table: every entry that can be opened under its current host is sealed again with a fresh nonce;
    // all others (orphans, refused entries) are carried over byte for byte.
    private Dictionary<string, Entry> SnapshotForWrite()
    {
        lock (_lock)
        {
            var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var (peerKey, entry) in _entries)
            {
                var credential = ResolveLocked(peerKey, entry);
                var host = entry.OpenedUnderHost;
                next[peerKey] = credential is not null && host is not null
                    ? new Entry(Seal(Encode(credential), peerKey, host), credential, host)
                    : new Entry(entry.Sealed, entry.Credential, entry.OpenedUnderHost) { Failed = entry.Failed, FailedUnderHost = entry.FailedUnderHost };
            }

            return next;
        }
    }

    private void WriteAll(Dictionary<string, Entry> next)
    {
        var document = new SecretsDocument
        {
            Schema = FileSchema,
            Check = SealWith([.. CheckPlaintext], CheckAad()),
            Peers = next.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value.Sealed, StringComparer.Ordinal)
        };
        SecureDirectory.Ensure(_paths.Root);
        var outcome = AtomicFile.Write(_paths.SecretsFile, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), backupPath: null, _atomicHook);
        if (outcome != AtomicWriteOutcome.Normal)
        {
            _logger.LogWarning("Shoal wrote secrets.enc without a hard-link backup ({Outcome})", outcome);
        }

        lock (_lock)
        {
            _entries = next;
        }

        _volumeWarning ??= VolumeCheck.CheckFileMode(_paths.SecretsFile);
    }

    private void ClearKeyLocked()
    {
        if (_aesKey is not null)
        {
            CryptographicOperations.ZeroMemory(_aesKey);
            _aesKey = null;
        }
    }

    private readonly struct Zeroing : IDisposable
    {
        private readonly byte[] _bytes;

        public Zeroing(byte[] bytes) => _bytes = bytes;

        public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
    }

    private sealed class Entry
    {
        public Entry(SealedBox sealedBox, PeerCredential? credential, string? openedUnderHost)
        {
            Sealed = sealedBox;
            Credential = credential;
            OpenedUnderHost = openedUnderHost;
        }

        public SealedBox Sealed { get; }

        public PeerCredential? Credential { get; set; }

        public string? OpenedUnderHost { get; set; }

        public bool Failed { get; set; }

        public string? FailedUnderHost { get; set; }
    }

    private sealed class SecretsDocument
    {
        public int Schema { get; set; }

        public SealedBox? Check { get; set; }

        public Dictionary<string, SealedBox>? Peers { get; set; }
    }

    private sealed class SealedBox
    {
        public string? Nonce { get; set; }

        public string? Ciphertext { get; set; }
    }

    private sealed class Plain
    {
        public string Token { get; set; } = string.Empty;

        public string DeviceId { get; set; } = string.Empty;

        public DateTime PairedUtc { get; set; }

        public PairingMethod Method { get; set; }
    }
}
