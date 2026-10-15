using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Shoal.Security;

/// <summary>
/// Wraps the master key for storage in <c>master.key</c> (Windows: DPAPI).
/// </summary>
internal interface IKeyProtector
{
    /// <summary>Wraps a key.</summary>
    /// <param name="key">The 32 key bytes.</param>
    /// <returns>The wrapped blob.</returns>
    byte[] Protect(byte[] key);

    /// <summary>Unwraps a blob.</summary>
    /// <param name="blob">The wrapped blob.</param>
    /// <returns>The key bytes.</returns>
    /// <exception cref="CryptographicException">The blob cannot be unwrapped by this user (another account, another machine, damaged).</exception>
    byte[] Unprotect(byte[] blob);
}

/// <summary>
/// The Win32 <c>DATA_BLOB</c> structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DataBlob
{
    /// <summary>The length of <see cref="Data"/> in bytes.</summary>
    public uint Size;

    /// <summary>The first byte.</summary>
    public byte* Data;
}

/// <summary>
/// The DPAPI entry points (<c>crypt32.dll</c>).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeDpapi
{
    /// <summary>Encrypts a blob for the current user.</summary>
    /// <param name="dataIn">The plain data.</param>
    /// <param name="description">Unused description.</param>
    /// <param name="entropy">Application entropy.</param>
    /// <param name="reserved">Must be zero.</param>
    /// <param name="prompt">Must be zero.</param>
    /// <param name="flags">Flags.</param>
    /// <param name="dataOut">The protected data (free with <see cref="LocalFree"/>).</param>
    /// <returns>True on success.</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    /// <summary>Decrypts a blob for the current user.</summary>
    /// <param name="dataIn">The protected data.</param>
    /// <param name="description">Unused.</param>
    /// <param name="entropy">Application entropy.</param>
    /// <param name="reserved">Must be zero.</param>
    /// <param name="prompt">Must be zero.</param>
    /// <param name="flags">Flags.</param>
    /// <param name="dataOut">The plain data (free with <see cref="LocalFree"/>).</param>
    /// <returns>True on success.</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    /// <summary>Frees memory that DPAPI allocated.</summary>
    /// <param name="handle">The pointer.</param>
    /// <returns>Zero on success.</returns>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    internal static partial IntPtr LocalFree(IntPtr handle);
}

/// <summary>
/// Windows DPAPI with scope <c>CurrentUser</c> (the service account) and application entropy (DESIGN 8.2). The blob can only be
/// unwrapped by the same account on the same machine, so switching Jellyfin between a tray app and a service, or between service
/// accounts, makes it unreadable on purpose: that is reported as <see cref="SecretUnreadableReason.KeyProtectionFailed"/>.
/// <para>
/// The entropy is a constant in an open-source plugin. It separates Shoal's blobs from other DPAPI users and <b>is not a secret</b>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiKeyProtector : IKeyProtector
{
    /// <summary>The application entropy. Public by design; see the class remarks.</summary>
    internal static readonly byte[] DefaultEntropy = Encoding.ASCII.GetBytes("shoal-master-key-v1 (public constant, not a secret)");

    // CRYPTPROTECT_UI_FORBIDDEN. CRYPTPROTECT_LOCAL_MACHINE (0x4) is deliberately never used: any local account could unwrap that.
    private const uint UiForbidden = 0x1;

    private readonly byte[] _entropy;

    /// <summary>
    /// Initializes a new instance of the <see cref="DpapiKeyProtector"/> class.
    /// </summary>
    public DpapiKeyProtector()
        : this(DefaultEntropy)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DpapiKeyProtector"/> class (tests: another entropy must not unwrap the blob).
    /// </summary>
    /// <param name="entropy">The entropy.</param>
    internal DpapiKeyProtector(byte[] entropy) => _entropy = entropy;

    /// <inheritdoc />
    public byte[] Protect(byte[] key) => Transform(key, protect: true, _entropy);

    /// <inheritdoc />
    public byte[] Unprotect(byte[] blob) => Transform(blob, protect: false, _entropy);

    private static unsafe byte[] Transform(byte[] input, bool protect, byte[] entropy)
    {
        fixed (byte* inputPointer = input)
        {
            fixed (byte* entropyPointer = entropy)
            {
                var inBlob = new DataBlob { Size = (uint)input.Length, Data = inputPointer };
                var entropyBlob = new DataBlob { Size = (uint)entropy.Length, Data = entropyPointer };
                DataBlob outBlob;
                var ok = protect
                    ? NativeDpapi.CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob)
                    : NativeDpapi.CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outBlob);
                if (!ok)
                {
                    throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                try
                {
                    var result = new byte[outBlob.Size];
                    Marshal.Copy((IntPtr)outBlob.Data, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    new Span<byte>(outBlob.Data, (int)outBlob.Size).Clear();
                    _ = NativeDpapi.LocalFree((IntPtr)outBlob.Data);
                }
            }
        }
    }
}
