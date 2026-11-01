using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Shoal.Api.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Shoal.LocalRemoval;

/// <summary>
/// 2.10.4 K3b: the append-only audit of every deletion, <c>local-delete-audit.jsonl</c> under the plugin data root, one JSON line per event
/// (<c>started</c> before the delete call, flushed to disk; <c>finished</c> when the item ends). W23-6: at <see cref="MaxBytes"/> the file is renamed to the next
/// free number (<c>.1</c> is the oldest), never over an older file; past <see cref="KeepFiles"/> old files the oldest one goes. Owner-only on Linux.
/// The local path is written here only (an admin file on this machine); no route serves this file, and no token, key or secret is in any entry.
/// The same event goes to the Jellyfin log without the path.
/// </summary>
internal sealed class LocalDeleteAudit : ILocalDeleteAudit
{
    /// <summary>The file name under the plugin data root.</summary>
    public const string FileName = "local-delete-audit.jsonl";

    /// <summary>The size at which the file is rotated.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>How many rotated files are kept by default.</summary>
    public const int DefaultKeepFiles = 20;

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _path;
    private readonly long _cap;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>Initializes a new instance of the <see cref="LocalDeleteAudit"/> class.</summary>
    /// <param name="root">The plugin data root (<c>ShoalPaths.Root</c>).</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The Jellyfin log (no paths).</param>
    public LocalDeleteAudit(string root, TimeProvider time, ILogger<LocalDeleteAudit>? logger = null)
        : this(root, time, MaxBytes, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="LocalDeleteAudit"/> class with a test cap.</summary>
    /// <param name="root">The folder.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cap">The rotation size.</param>
    /// <param name="logger">The log.</param>
    internal LocalDeleteAudit(string root, TimeProvider time, long cap, ILogger? logger)
    {
        _path = Path.Combine(root, FileName);
        _cap = cap;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets or sets how many rotated files are kept.</summary>
    internal int KeepFiles { get; set; } = DefaultKeepFiles;

    /// <summary>Gets the file path.</summary>
    internal string FilePath => _path;

    /// <inheritdoc />
    /// <exception cref="IOException">The line could not be written; the caller must not delete.</exception>
    public void Started(LocalDeleteAuditEntry entry)
    {
        Append("started", entry);
        _logger.LogInformation("Shoal is deleting a local movie (batch {Batch}, item {Item}, {Bytes} bytes)", entry.BatchId, entry.ItemKey, entry.Bytes);
    }

    /// <inheritdoc />
    public void Finished(LocalDeleteAuditEntry entry)
    {
        try
        {
            Append("finished", entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Shoal could not write the end of a deletion to its audit file ({Kind})", ex.GetType().Name);
        }

        _logger.LogInformation("Shoal deletion ended (batch {Batch}, item {Item}): {Outcome} {Reason} {Shared} {History}", entry.BatchId, entry.ItemKey, Word(entry.Outcome), Word(entry.Reason), Word(entry.Shared), Word(entry.History));
    }

    private static string? Word<T>(T? value)
        where T : struct, Enum => value is { } v ? JsonSerializer.Serialize(v, AdminJson.Options).Trim('"') : null;

    private void Append(string kind, LocalDeleteAuditEntry entry)
    {
        var line = JsonSerializer.Serialize(
            new
            {
                utc = _time.GetUtcNow(),
                @event = kind,
                batchId = entry.BatchId,
                itemKey = entry.ItemKey,
                adminId = entry.AdminId.ToString("N"),
                peerKey = entry.PeerKey,
                title = entry.Title,
                year = entry.Year,
                bytes = entry.Bytes,
                sha256Prefix = entry.Sha256Prefix,
                path = entry.Path,
                outcome = Word(entry.Outcome),
                reason = Word(entry.Reason),
                shared = Word(entry.Shared),
                history = Word(entry.History),
            },
            AdminJson.Options) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (_lock)
        {
            var current = new FileInfo(_path);
            if (current.Exists && current.Length + bytes.Length > _cap)
            {
                Rotate();
            }

            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerOnly;
            }

            using (var stream = new FileStream(_path, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(_path) != OwnerOnly)
            {
                File.SetUnixFileMode(_path, OwnerOnly); // a file made by an earlier build with the default umask
            }
        }
    }

    // W23-6: the next number after the highest one on disk; File.Move without overwrite, so an IOException (and no delete) beats losing history.
    private void Rotate()
    {
        var folder = Path.GetDirectoryName(_path)!;
        var numbers = Directory.EnumerateFiles(folder, FileName + ".*")
            .Select(static f => Path.GetFileName(f)).Where(static f => f.Length > FileName.Length + 1 && f.StartsWith(FileName + ".", StringComparison.Ordinal)) // Windows also matches the bare name
            .Select(static f => int.TryParse(f.AsSpan(FileName.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Where(static n => n > 0).Order().ToList();
        var next = numbers.Count == 0 ? 1 : numbers[^1] + 1;
        File.Move(_path, _path + "." + next.ToString(CultureInfo.InvariantCulture), overwrite: false);
        numbers.Add(next);
        foreach (var old in numbers.Take(Math.Max(0, numbers.Count - Math.Max(1, KeepFiles))))
        {
            try
            {
                File.Delete(_path + "." + old.ToString(CultureInfo.InvariantCulture));
                _logger.LogInformation("Shoal removed its oldest delete audit file (number {Number}, more than {Keep} kept)", old, KeepFiles);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Shoal could not remove an old delete audit file ({Kind})", ex.GetType().Name);
            }
        }
    }
}
