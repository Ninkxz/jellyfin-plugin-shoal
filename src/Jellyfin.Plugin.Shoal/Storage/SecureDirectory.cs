using System;
using System.IO;

namespace Jellyfin.Plugin.Shoal.Storage;

/// <summary>
/// Creates the folders Shoal owns with mode 0700 (DESIGN 3.1). On Windows the mode is ignored; the DPAPI-wrapped key and the
/// user's own profile permissions apply there.
/// </summary>
internal static class SecureDirectory
{
    /// <summary>The Unix mode of every folder Shoal creates: owner only.</summary>
    public const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates the folder (and parents) and, on Unix, makes sure the folder itself is 0700.
    /// </summary>
    /// <param name="path">The folder.</param>
    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            try
            {
                if (File.GetUnixFileMode(path) != PrivateDirectoryMode)
                {
                    File.SetUnixFileMode(path, PrivateDirectoryMode);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours to change (for example a mounted volume); the volume check reports it.
            }
            catch (IOException)
            {
            }
        }
    }
}
