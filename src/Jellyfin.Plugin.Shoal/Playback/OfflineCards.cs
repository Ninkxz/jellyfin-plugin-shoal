using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Shoal.Http;
using Jellyfin.Plugin.Shoal.Storage;
using Jellyfin.Plugin.Shoal.Sync;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

#pragma warning disable SA1201 // The file reads top to bottom: the words, the encoder, the service, the routes, the result.
#pragma warning disable SA1202 // Arguments is internal for the tests, next to the method that uses it.
#pragma warning disable SA1600 // OfflineCardResult is internal plumbing; the public surface is documented.

namespace Jellyfin.Plugin.Shoal.Playback;

/// <summary>
/// The offline card settings (<c>OfflineCardEnabled</c>, <c>RequestServiceName</c>, <c>RequestServiceUrl</c>).
/// </summary>
/// <param name="Enabled">Whether an offline friend gets the card instead of a bare "playback error".</param>
/// <param name="ServiceName">The name of the request service ("Seerr"), plain words only.</param>
/// <param name="ServiceUrl">Where requests are made, or empty. Shown in the admin page and in the toast; never in the video.</param>
public sealed record OfflineCardSettings(bool Enabled, string ServiceName, string ServiceUrl);

/// <summary>
/// The words of the offline card and of the toast that goes with it. Simple words, no dashes.
/// </summary>
public static class OfflineCardCopy
{
    /// <summary>The version of the wording and layout; part of the cache key, so a change here regenerates every clip.</summary>
    public const string Version = "1";

    /// <summary>What the card says when the friend's name is not known.</summary>
    public const string GenericFriend = "This friend's server";

    /// <summary>
    /// The paragraphs on the card.
    /// </summary>
    /// <param name="friend">The friend's display name (already cleaned), or empty.</param>
    /// <param name="service">The request service name.</param>
    /// <returns>Four paragraphs.</returns>
    public static IReadOnlyList<string> Paragraphs(string friend, string service) =>
    [
        (friend.Length == 0 ? GenericFriend : friend) + " is offline right now.",
        "This is not a problem with this server. Their server is down.",
        "To watch this title now, request it on " + service + " and it will be downloaded here. Strongly recommended.",
        "Or try again later.",
    ];

    /// <summary>
    /// The message core shows on the user's sessions when a play request fails because the friend is offline.
    /// </summary>
    /// <param name="friend">The friend's display name (already cleaned).</param>
    /// <param name="service">The request service name.</param>
    /// <param name="url">The request service address, or empty.</param>
    /// <returns>One or two sentences.</returns>
    public static string Toast(string friend, string service, string url)
    {
        var text = (friend.Length == 0 ? GenericFriend : friend) + " is offline right now. Try again later.";
        return url.Length == 0 ? text : text + " Request it on " + service + ": " + url;
    }

    /// <summary>
    /// Lays the paragraphs out as the lines the card shows: each paragraph wrapped at a width, a blank line between paragraphs.
    /// </summary>
    /// <param name="paragraphs">The paragraphs.</param>
    /// <param name="width">The most characters on a line.</param>
    /// <returns>The text with <c>\n</c> line ends.</returns>
    public static string Layout(IEnumerable<string> paragraphs, int width = 44)
    {
        ArgumentNullException.ThrowIfNull(paragraphs);
        var blocks = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                }

                if (line.Length > 0)
                {
                    line.Append(' ');
                }

                line.Append(word);
            }

            if (line.Length > 0)
            {
                lines.Add(line.ToString());
            }

            blocks.Add(string.Join('\n', lines));
        }

        return string.Join("\n\n", blocks);
    }
}

/// <summary>
/// Makes the clip of an offline card (a seam: the real one is Jellyfin's ffmpeg).
/// </summary>
public interface IOfflineCardEncoder
{
    /// <summary>Gets a value indicating whether a clip can be made at all (ffmpeg is there).</summary>
    bool Available { get; }

    /// <summary>
    /// Makes an MP4 of 1280 by 720, about 8 seconds, H.264 and a silent AAC track, showing the text.
    /// </summary>
    /// <param name="text">The text, laid out in lines (<see cref="OfflineCardCopy.Layout"/>).</param>
    /// <param name="outputPath">Where to write the MP4.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True when the file was written.</returns>
    Task<bool> EncodeAsync(string text, string outputPath, CancellationToken cancellationToken);
}

/// <summary>
/// Makes the card with the host's ffmpeg: a plain background, the text drawn on it with <c>drawtext</c>, and a silent audio track. Nothing a peer sent is
/// used except the friend's cleaned display name, which goes into a text file and never into a command line or a filter string.
/// </summary>
public sealed class FfmpegCardEncoder : IOfflineCardEncoder
{
    private static readonly string[] FontCandidates =
    [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/liberation/LiberationSans-Bold.ttf",
        "/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf",
        "/usr/share/fonts/noto/NotoSans-Bold.ttf",
        "/usr/share/fonts/opentype/noto/NotoSans-Bold.ttf",
        "/System/Library/Fonts/Supplemental/Arial Bold.ttf",
        "C:/Windows/Fonts/arialbd.ttf",
    ];

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly IEncoderLocator _locator;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegCardEncoder"/> class.
    /// </summary>
    /// <param name="locator">Where the host's ffmpeg is.</param>
    /// <param name="logger">The logger.</param>
    public FfmpegCardEncoder(IEncoderLocator locator, ILogger<FfmpegCardEncoder> logger)
        : this(locator, (ILogger)logger)
    {
    }

    internal FfmpegCardEncoder(IEncoderLocator locator, ILogger logger)
    {
        _locator = locator;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Available => _locator.EncoderPath is { Length: > 0 } path && (!Path.IsPathRooted(path) || File.Exists(path));

    /// <summary>
    /// The ffmpeg arguments for a card (public to the tests: the command line is the part that cannot be run without ffmpeg).
    /// </summary>
    /// <param name="textFile">The file that holds the text.</param>
    /// <param name="outputPath">The MP4 to write.</param>
    /// <param name="fontFile">A font file, or null for fontconfig's default.</param>
    /// <returns>The arguments, one per item.</returns>
    internal static IReadOnlyList<string> Arguments(string textFile, string outputPath, string? fontFile)
    {
        var font = fontFile is null ? "font='Sans'" : "fontfile='" + FilterPath(fontFile) + "'";
        var draw = "drawtext=" + font + ":textfile='" + FilterPath(textFile) + "':expansion=none:fontcolor=white:fontsize=44:line_spacing=14:x=(w-text_w)/2:y=(h-text_h)/2";
        return
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=c=0x14202b:s=1280x720:r=25:d=8",
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
            "-vf", draw,
            "-t", "8",
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "main", "-level", "3.1", "-pix_fmt", "yuv420p", "-g", "25", "-crf", "28",
            "-c:a", "aac", "-b:a", "48k", "-ar", "44100", "-ac", "2",
            "-shortest", "-movflags", "+faststart",
            outputPath,
        ];
    }

    /// <inheritdoc />
    public async Task<bool> EncodeAsync(string text, string outputPath, CancellationToken cancellationToken)
    {
        var encoder = _locator.EncoderPath;
        if (!Available || string.IsNullOrWhiteSpace(encoder))
        {
            return false;
        }

        var work = Path.Combine(Path.GetDirectoryName(outputPath)!, "work-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant());
        const string TextName = "card.txt";
        const string ClipName = "card.mp4";
        try
        {
            SecureDirectory.Ensure(work);
            await File.WriteAllTextAsync(Path.Combine(work, TextName), text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var temp = Path.Combine(work, ClipName);
            var start = new ProcessStartInfo(encoder)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = work,
            };

            // Relative names, with the work folder as the working directory: no host path (which may hold a quote or a colon) goes through filter escaping.
            foreach (var argument in Arguments(TextName, ClipName, FontCandidates.FirstOrDefault(File.Exists)))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning("The offline card could not start ffmpeg ({Error})", ex.GetType().Name);
                return false;
            }

            var drain = Task.Run(
                async () =>
                {
                    var buffer = new char[1024];
                    while (await process.StandardError.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false) > 0)
                    {
                    }
                },
                CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Limit);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                _logger.LogWarning("The offline card took too long and was stopped");
                return false;
            }

            await drain.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(temp))
            {
                _logger.LogWarning("The offline card could not be made (ffmpeg exit {Code}); is a font installed?", process.ExitCode);
                return false;
            }

            File.Move(temp, outputPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The offline card could not be written ({Error})", ex.GetType().Name);
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover folder of a few hundred bytes; the next run makes its own.
            }
        }
    }

    // A path inside a filter option: forward slashes, and the colon of a drive letter escaped.
    private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal);

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// The finished card.
/// </summary>
/// <param name="Bytes">The MP4.</param>
/// <param name="Etag">A short hash of the bytes.</param>
#pragma warning disable CA1819 // A clip is a few KB, read-only after it is made, and handed to the response as is.
public sealed record OfflineClip(byte[] Bytes, string Etag);
#pragma warning restore CA1819

/// <summary>
/// The offline card (owner request 2026-10-16): when a friend's server is down, PlaybackInfo offers a short video that says so instead of a bare error. The clip
/// is made once per friend name and wording, kept in memory and in a small folder, and served by Shoal's own static route; no peer is called, no stream slot is
/// taken, no grant is made, and the reports a player sends for it are swallowed so the real title never gets a resume point or a watched mark.
/// </summary>
public sealed class OfflineCards : IDisposable
{
    /// <summary>The length of the clip.</summary>
    public const long RunTimeTicks = 8 * TimeSpan.TicksPerSecond;

    /// <summary>The title clients show for the card's media source.</summary>
    public const string SourceName = "Server offline";

    private const string SessionPrefix = "oc";
    private const int MaxSessions = 1000;
    private const int MaxSessionsPerUser = 8;
    private const int MaxClips = 64;
    private const int MinBytes = 1024;
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan SessionLife = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GenerationLimit = TimeSpan.FromSeconds(20);

    private readonly Func<OfflineCardSettings> _settings;
    private readonly IOfflineCardEncoder _encoder;
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, OfflineClip> _clips = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Guid User, Guid Item, DateTimeOffset Expires)> _sessions = new(StringComparer.Ordinal);
    private readonly object _sessionLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _busyKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="OfflineCards"/> class.
    /// </summary>
    /// <param name="settings">Reads the settings.</param>
    /// <param name="encoder">Makes the clips.</param>
    /// <param name="folder">Where the clips are kept.</param>
    /// <param name="time">The clock.</param>
    public OfflineCards(Func<OfflineCardSettings> settings, IOfflineCardEncoder encoder, string folder, TimeProvider time)
    {
        _settings = settings;
        _encoder = encoder;
        _folder = folder;
        _time = time;
    }

    /// <summary>Gets a value indicating whether the card is on and a clip can be made.</summary>
    public bool Enabled => _settings().Enabled && _encoder.Available;

    /// <summary>Gets the settings in force.</summary>
    public OfflineCardSettings Settings => _settings();

    /// <summary>Gets a value indicating whether any card session is live (the only time a playback report is looked at).</summary>
    public bool HasSessions => !_sessions.IsEmpty;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Forgets every clip, failure and session, and deletes the saved clips (tests, and a way to rebuild by hand).
    /// </summary>
    internal void Clear()
    {
        _clips.Clear();
        _failed.Clear();
        _sessions.Clear();
        if (Directory.Exists(_folder))
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "*.mp4"))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// The media source id of the card of an item: derived from the item id, so it needs no state and names no peer id.
    /// </summary>
    /// <param name="itemId">The local item id.</param>
    /// <returns>The id.</returns>
    public static Guid SourceIdOf(Guid itemId) => new(SHA256.HashData(Encoding.UTF8.GetBytes("shoal-offline-card|" + itemId.ToString("N"))).AsSpan(0, 16));

    /// <summary>
    /// Whether a generated file is the MP4 it should be: a sensible size, an <c>ftyp</c> box first, and a <c>moov</c> box.
    /// </summary>
    /// <param name="bytes">The file.</param>
    /// <returns>True for a plausible MP4.</returns>
    public static bool LooksLikeMp4(ReadOnlySpan<byte> bytes) =>
        bytes.Length is >= MinBytes and <= MaxBytes && bytes[4..8].SequenceEqual("ftyp"u8) && bytes.IndexOf("moov"u8) > 0;

    /// <summary>
    /// Gets the clip for a friend, making it when it is not there yet.
    /// </summary>
    /// <param name="friend">The friend's display name (cleaned).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The clip, or null when it cannot be made (the caller falls back to the plain refusal).</returns>
    public async Task<OfflineClip?> GetAsync(string friend, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var name = Clean(friend);
        var key = KeyOf(name, settings.ServiceName);
        if (_clips.TryGetValue(key, out var warm))
        {
            return warm;
        }

        var now = _time.GetUtcNow();
        if (_failed.TryGetValue(key, out var failedAt) && now - failedAt < RetryAfterFailure)
        {
            return null;
        }

        // One clip is made at a time. A request for another clip while one is being made gets the plain refusal at once (the next try finds it ready); a request for
        // the same clip waits for it, so no queue of tasks builds up behind ffmpeg (Warden 15 L2).
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(Volatile.Read(ref _busyKey), key, StringComparison.Ordinal))
            {
                return null;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(GenerationLimit);
            try
            {
                await _gate.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        Volatile.Write(ref _busyKey, key);
        try
        {
            if (_clips.TryGetValue(key, out warm))
            {
                return warm;
            }

            var path = Path.Combine(_folder, key + ".mp4");
            if (File.Exists(path) && await TryLoadAsync(path, cancellationToken).ConfigureAwait(false) is { } kept)
            {
                return Remember(key, kept);
            }

            if (!_encoder.Available)
            {
                return null;
            }

            SecureDirectory.Ensure(_folder);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(GenerationLimit);
            bool made;
            try
            {
                made = await _encoder.EncodeAsync(OfflineCardCopy.Layout(OfflineCardCopy.Paragraphs(name, settings.ServiceName)), path, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                made = false;
            }

            if (made && await TryLoadAsync(path, cancellationToken).ConfigureAwait(false) is { } fresh)
            {
                _failed.TryRemove(key, out _);
                return Remember(key, fresh);
            }

            _failed[key] = now;
            return null;
        }
        finally
        {
            Volatile.Write(ref _busyKey, null);
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts a card session: the play session id the client will report with. It is random and Home's own, it names no peer, and it belongs to one user and one
    /// item: reports are swallowed only for that item (Warden 15 M1). A user holds at most 8; a ninth replaces that user's own oldest, never another user's.
    /// </summary>
    /// <param name="userId">The user who asked.</param>
    /// <param name="itemId">The item the card replaces.</param>
    /// <returns>The id, or null when the table is full of other users' live sessions (the caller gives the plain refusal).</returns>
    public string? NewSession(Guid userId, Guid itemId)
    {
        var now = _time.GetUtcNow();
        lock (_sessionLock)
        {
            foreach (var old in _sessions.Where(p => p.Value.Expires <= now).Select(static p => p.Key).ToList())
            {
                _sessions.TryRemove(old, out _);
            }

            var mine = _sessions.Where(p => p.Value.User == userId).ToList();
            if (mine.Count >= MaxSessionsPerUser)
            {
                _sessions.TryRemove(mine.MinBy(static p => p.Value.Expires).Key, out _);
            }
            else if (_sessions.Count >= MaxSessions)
            {
                return null;
            }

            var id = SessionPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(15)).ToLowerInvariant();
            _sessions[id] = (userId, itemId, now + SessionLife);
            return id;
        }
    }

    /// <summary>
    /// Ends a card session (the player reported that it stopped).
    /// </summary>
    /// <param name="playSessionId">The id.</param>
    public void Drop(string? playSessionId)
    {
        if (playSessionId is not null)
        {
            _sessions.TryRemove(playSessionId, out _);
        }
    }

    /// <summary>
    /// Whether a play session id is a card session of this user.
    /// </summary>
    /// <param name="playSessionId">The id a player reported.</param>
    /// <param name="userId">The reporting user.</param>
    /// <param name="itemId">The item the report is about; null for a ping, which carries no item.</param>
    /// <returns>True for a live card session of that user and that item.</returns>
    public bool IsCardSession(string? playSessionId, Guid userId, Guid? itemId) =>
        playSessionId is { Length: 32 } id && id.StartsWith(SessionPrefix, StringComparison.Ordinal)
        && _sessions.TryGetValue(id, out var session) && session.User == userId && session.Expires > _time.GetUtcNow()
        && (itemId is null || itemId == session.Item);

    /// <summary>
    /// The PlaybackInfo answer for the card: one ordinary-looking direct-play source titled "Server offline".
    /// </summary>
    /// <param name="itemId">The item the client asked for.</param>
    /// <param name="clip">The clip.</param>
    /// <param name="playSessionId">The card session.</param>
    /// <returns>The answer.</returns>
    public static PlaybackInfoResponse BuildResponse(Guid itemId, OfflineClip clip, string playSessionId)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var bitrate = (int)Math.Min(int.MaxValue, clip.Bytes.Length * 8L / 8);
        var source = new MediaSourceInfo
        {
            Id = SourceIdOf(itemId).ToString("N"),
            Protocol = MediaProtocol.File,
            IsRemote = false,
            Path = null,
            Container = "mp4",
            Size = clip.Bytes.Length,
            Bitrate = bitrate,
            RunTimeTicks = RunTimeTicks,
            Name = SourceName,
            ETag = clip.Etag,
            DefaultAudioStreamIndex = 1,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,
            SupportsTranscoding = false,
            RequiredHttpHeaders = [],
            MediaStreams =
            [
                new MediaStream
                {
                    Index = 0,
                    Type = MediaStreamType.Video,
                    Codec = "h264",
                    Profile = "Main",
                    Level = 31,
                    Width = 1280,
                    Height = 720,
                    AspectRatio = "16:9",
                    RealFrameRate = 25,
                    AverageFrameRate = 25,
                    PixelFormat = "yuv420p",
                    BitDepth = 8,
                    IsDefault = true,
                },
                new MediaStream
                {
                    Index = 1,
                    Type = MediaStreamType.Audio,
                    Codec = "aac",
                    Profile = "LC",
                    Channels = 2,
                    ChannelLayout = "stereo",
                    SampleRate = 44100,
                    IsDefault = true,
                },
            ],
            RequiresOpening = false,
            RequiresClosing = false,
            IsInfiniteStream = false,
            SupportsProbing = false,
            HasSegments = false,
        };
        return new PlaybackInfoResponse { MediaSources = [source], PlaySessionId = playSessionId };
    }

    // The cleaned display name, short enough for one line of the card.
    private static string Clean(string friend) => MetadataSanitizer.FriendName(friend) is { Length: > 0 } name ? (name.Length > 40 ? name[..40].TrimEnd() : name) : string.Empty;

    private static string KeyOf(string name, string service) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OfflineCardCopy.Version + "|" + name + "|" + service)))[..24];

    private static async Task<OfflineClip?> TryLoadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < MinBytes or > MaxBytes)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return LooksLikeMp4(bytes) ? new OfflineClip(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes))[..32]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private OfflineClip Remember(string key, OfflineClip clip)
    {
        if (_clips.Count >= MaxClips)
        {
            _clips.Clear();
        }

        _clips[key] = clip;
        return clip;
    }
}

/// <summary>
/// The core playback report routes (start, progress, ping, stopped; the current and the older shapes). A player that was given the offline card reports
/// against the real item; Shoal answers those reports itself so nothing is written to the item.
/// </summary>
public static class PlaybackReportRoutes
{
    private static readonly HashSet<string> Shapes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sessions/Playing",
        "Sessions/Playing/Progress",
        "Sessions/Playing/Ping",
        "Sessions/Playing/Stopped",
        "PlayingItems/{}",
        "PlayingItems/{}/Progress",
        "Users/{}/PlayingItems/{}",
        "Users/{}/PlayingItems/{}/Progress",
    };

    /// <summary>
    /// Whether a report ends the playback (the stopped route, or a DELETE of the older shape).
    /// </summary>
    /// <param name="template">The attribute route template.</param>
    /// <param name="method">The HTTP method.</param>
    /// <returns>True for a stop.</returns>
    public static bool IsStop(string? template, string method) =>
        HttpMethods.IsDelete(method) || (template is not null && template.TrimEnd('/').EndsWith("/Stopped", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether an action template is one of the playback report routes.
    /// </summary>
    /// <param name="template">The attribute route template.</param>
    /// <returns>True for a report route.</returns>
    public static bool Matches(string? template) =>
        !string.IsNullOrEmpty(template)
        && Shapes.Contains(string.Join('/', template.Trim('/').Split('/').Select(static s => s.StartsWith('{') ? "{}" : s)));
}

/// <summary>
/// Serves the card's bytes on the static stream route: the same headers as any direct-play stream (Shoal's own type and security headers), one range at most.
/// </summary>
internal sealed class OfflineCardResult : IActionResult
{
    private readonly OfflineClip _clip;

    public OfflineCardResult(OfflineClip clip) => _clip = clip;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var response = http.Response;
        var length = (long)_clip.Bytes.Length;
        if (!StaticRange.TryParse(http.Request.Headers.Range, out var range))
        {
            ResponseWriter.SetValidatedMediaHeaders(response, "bytes */" + length.ToString(CultureInfo.InvariantCulture), "bytes", null);
            ResponseWriter.WriteStatus(response, StatusCodes.Status416RangeNotSatisfiable);
            return;
        }

        long start = 0;
        var end = length - 1;
        var status = StatusCodes.Status200OK;
        if (range is not null)
        {
            if (range.Start is { } first)
            {
                start = first;
                end = Math.Min(range.End ?? end, end);
            }
            else
            {
                start = Math.Max(0, length - range.End!.Value);
            }

            if (start >= length || end < start)
            {
                ResponseWriter.SetValidatedMediaHeaders(response, "bytes */" + length.ToString(CultureInfo.InvariantCulture), "bytes", null);
                ResponseWriter.WriteStatus(response, StatusCodes.Status416RangeNotSatisfiable);
                return;
            }

            status = StatusCodes.Status206PartialContent;
        }

        response.StatusCode = status;
        ResponseWriter.Apply(response, ResponseKind.StaticStream, "mp4");
        ResponseWriter.SetValidatedMediaHeaders(
            response,
            status == StatusCodes.Status206PartialContent ? string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{end}/{length}") : null,
            "bytes",
            null);
        response.ContentLength = end - start + 1;
        if (!HttpMethods.IsHead(http.Request.Method))
        {
            await response.Body.WriteAsync(_clip.Bytes.AsMemory((int)start, (int)(end - start + 1)), http.RequestAborted).ConfigureAwait(false);
        }
    }
}
