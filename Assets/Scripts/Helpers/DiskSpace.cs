using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Helpers
{
    /// <summary>
    /// Free disk space for a path, cheap enough to call while a command builds its reply. Asks the OS directly
    /// rather than through <see cref="DriveInfo"/>, whose first call under Mono enumerates every drive and is slow.
    /// Windows only; elsewhere the answer is "unknown".
    /// </summary>
    public static class DiskSpace
    {
        /// <summary>The bytes available to this process on the volume holding <paramref name="path"/>.</summary>
        /// <param name="path">Any path on the volume; it need not exist.</param>
        /// <param name="freeBytes">The available bytes on success.</param>
        /// <returns>False when the platform or the volume cannot answer.</returns>
        public static bool TryGetFreeBytes(string path, out long freeBytes)
        {
            freeBytes = 0;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || !GetDiskFreeSpaceEx(root, out ulong available, out _, out _))
                    return false;

                freeBytes = available > long.MaxValue ? long.MaxValue : (long)available;
                return true;
            }
            catch (Exception)
            {
                // Best effort: a malformed path only loses the caller's warning.
                return false;
            }
#else
            return false;
#endif
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
#endif
    }
}
