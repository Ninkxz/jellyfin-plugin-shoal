using System;

namespace Jellyfin.Plugin.Shoal.Sync;

/// <summary>
/// The disk budget of one peer (DESIGN 5.10): stubs and images, measured as bytes Shoal wrote. Images stop at the limit and titles still
/// import with placeholder art. One instance covers one peer; the sync loads its starting usage from <c>catalog.usage.diskBytes</c> and
/// saves <see cref="UsedBytes"/> back.
/// </summary>
public sealed class DiskQuota
{
    private readonly object _gate = new();
    private long _used;
    private bool _exhausted;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiskQuota"/> class.
    /// </summary>
    /// <param name="limitBytes">The most bytes Shoal may write for this peer.</param>
    /// <param name="usedBytes">The bytes already written.</param>
    public DiskQuota(long limitBytes, long usedBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(usedBytes);
        LimitBytes = limitBytes;
        _used = usedBytes;
    }

    /// <summary>Gets the limit in bytes.</summary>
    public long LimitBytes { get; }

    /// <summary>Gets the bytes written so far.</summary>
    public long UsedBytes
    {
        get
        {
            lock (_gate)
            {
                return _used;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether an image was refused (or the budget was found full) during this run. Once set, the sync fetches no
    /// more images and raises the alert once.
    /// </summary>
    public bool Exhausted
    {
        get
        {
            lock (_gate)
            {
                return _exhausted || _used >= LimitBytes;
            }
        }
    }

    /// <summary>
    /// The limit for a setting in gigabytes (<c>DiskQuotaGbPerPeer</c>, 1 to 50).
    /// </summary>
    /// <param name="gigabytes">The setting.</param>
    /// <returns>Bytes (1 GB is 2^30).</returns>
    public static long LimitFromGigabytes(int gigabytes) => Math.Clamp(gigabytes, 1, 50) * (1L << 30);

    /// <summary>
    /// Reserves room for a file about to be written. Called before each image write.
    /// </summary>
    /// <param name="bytes">The size of the new file.</param>
    /// <param name="replacing">The size of the file it replaces, which is freed by the write.</param>
    /// <returns>True when it fits; false marks the quota exhausted and the caller writes nothing.</returns>
    public bool TryReserve(long bytes, long replacing = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(replacing);
        lock (_gate)
        {
            var net = bytes - replacing;
            if (net > 0 && _used + net > LimitBytes)
            {
                _exhausted = true;
                return false;
            }

            _used = Math.Max(0, _used + net);
            return true;
        }
    }

    /// <summary>
    /// Counts bytes that must be written whatever the budget says (the <c>.strm</c> and <c>.nfo</c> of a title): they are small, and a
    /// full quota never stops titles from importing.
    /// </summary>
    /// <param name="bytes">The bytes written (negative when files were removed).</param>
    public void Add(long bytes)
    {
        lock (_gate)
        {
            _used = Math.Max(0, _used + bytes);
        }
    }
}
