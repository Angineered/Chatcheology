using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArchiveRefresh.PhonePull;

/// <summary>Makes NTFS hard links. Tests give a linker that cannot link, to exercise the local-copy fallback.</summary>
internal interface IFileLinker
{
    /// <summary>True when links between the two folders are likely possible (same volume, a file system with links).
    /// Used only for the free-space estimate; <see cref="TryLink"/> decides.</summary>
    bool LikelySameVolume(string existingFolder, string newFolder);

    /// <summary>Makes <paramref name="newPath"/> a hard link to <paramref name="existingPath"/> (which is not changed).
    /// False when this volume pair or file system cannot link; any other failure throws.</summary>
    bool TryLink(string existingPath, string newPath);
}

/// <summary>Facts about the volume holding a folder.</summary>
internal static class Volumes
{
    internal static string? Format(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).DriveFormat;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Bytes per cluster (GetDiskFreeSpaceW); 4096 when the volume does not say.</summary>
    internal static long ClusterBytes(string folder)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));

        if (!string.IsNullOrEmpty(root)
            && NativeMethods.GetDiskFreeSpace(root.EndsWith('\\') ? root : root + "\\", out var sectorsPerCluster, out var bytesPerSector, out _, out _)
            && sectorsPerCluster > 0 && bytesPerSector > 0)
        {
            return (long)sectorsPerCluster * bytesPerSector;
        }

        return 4096;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpace(
            string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
    }
}

/// <summary>Windows hard links (CreateHardLinkW).</summary>
internal sealed class NtfsLinker : IFileLinker
{
    private const int ErrorInvalidFunction = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorTooManyLinks = 1142;

    public bool LikelySameVolume(string existingFolder, string newFolder)
    {
        var a = Path.GetPathRoot(Path.GetFullPath(existingFolder));
        var b = Path.GetPathRoot(Path.GetFullPath(newFolder));

        if (string.IsNullOrEmpty(a) || !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var format = new DriveInfo(a).DriveFormat;

            return format is "NTFS" or "ReFS";
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TryLink(string existingPath, string newPath)
    {
        if (NativeMethods.CreateHardLink(LongPath(newPath), LongPath(existingPath), IntPtr.Zero))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();

        return error is ErrorInvalidFunction or ErrorNotSameDevice or ErrorNotSupported or ErrorTooManyLinks
                   or ErrorAccessDenied or ErrorInvalidParameter
            ? false
            : throw new IOException("A hard link could not be made.", new Win32Exception(error));
    }

    /// <summary>How many names the file has (1 for a plain file; 2 or more once linked).</summary>
    internal static int LinkCount(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        return NativeMethods.GetFileInformationByHandle(handle, out var info)
            ? (int)info.NumberOfLinks
            : throw new IOException("The file's information could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    /// <summary>The path with the \\?\ prefix, so the native call takes paths longer than 260 characters.</summary>
    private static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);

        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
    }
}
