using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeDiffer.Core.Walk;

/// <summary>
/// A file's identity as seen through an OPEN HANDLE: the live values (the file-control-block), never a
/// directory entry's lazily-updated copy. While a writer holds a file open, NTFS — and SMB servers reading
/// NTFS — can report a stale size/mtime in directory listings; a handle query can't be stale. Ticks are UTC;
/// 0 = not available on this platform/server.
/// </summary>
public readonly record struct FileStat(long Length, long LastWriteUtcTicks, long ChangeUtcTicks, long FileId)
{
    /// <summary>Same file, unchanged, as the walk's listing row claimed? (FileId compared only when both known.)</summary>
    public bool Matches(FileEntry e)
        => Length == e.Length &&
           LastWriteUtcTicks == e.LastWriteTimeUtc.Ticks &&
           ChangeUtcTicks == e.ChangeTimeUtcTicks &&
           (FileId == 0 || e.FileId == 0 || FileId == e.FileId);

    /// <summary>Stat an open handle. Off Windows, only length + mtime are available (ChangeTime/FileId = 0).</summary>
    public static FileStat Of(SafeFileHandle h, string path)
    {
        if (!OperatingSystem.IsWindows())
            return new FileStat(RandomAccess.GetLength(h), File.GetLastWriteTimeUtc(path).Ticks, 0, 0);

        if (!GetFileInformationByHandleEx(h, FileBasicInfoClass, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
            throw new IOException($"cannot stat {path} (win32 error {Marshal.GetLastWin32Error()})");
        long fileId = GetFileInformationByHandle(h, out var byHandle)
            ? ((long)byHandle.FileIndexHigh << 32) | byHandle.FileIndexLow
            : 0;
        return new FileStat(
            RandomAccess.GetLength(h),
            DirectoryLister.FileTimeToUtcTicks(basic.LastWriteTime),
            DirectoryLister.FileTimeToUtcTicks(basic.ChangeTime),
            fileId);
    }

    /// <summary>Open with FILE_READ_ATTRIBUTES only (no data access, never blocks a writer) and stat.</summary>
    public static FileStat OfPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var fi = new FileInfo(path);
            return new FileStat(fi.Length, fi.LastWriteTimeUtc.Ticks, 0, 0);
        }
        using var h = CreateFileW(DirectoryLister.LongPath(path), FileReadAttributes, 7 /*share all*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"cannot open {path} (win32 error {Marshal.GetLastWin32Error()})");
        return Of(h, path);
    }

    private const int FileBasicInfoClass = 0;
    private const uint FileReadAttributes = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }

    // FILETIME is two DWORDs (4-byte aligned), so Pack = 4 keeps the longs at the native offsets.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FileBasicInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
}
