using System.Collections.Concurrent;
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

    /// <summary>The same size, mtime and ChangeTime as the listing row, but another FileId: a server whose directory
    /// listing and open files number a file differently (seen on a share in use for a third of its files).</summary>
    public bool OnlyIdDiffers(FileEntry e)
        => Length == e.Length &&
           LastWriteUtcTicks == e.LastWriteTimeUtc.Ticks &&
           ChangeUtcTicks == e.ChangeTimeUtcTicks &&
           FileId != 0 && e.FileId != 0 && FileId != e.FileId;

    /// <summary>Stat an open handle. Off Windows, only length + mtime are available (ChangeTime/FileId = 0).
    /// One query (FileAllInformation: times, size and id at once) where the volume answers it, as NTFS and SMB do;
    /// else the separate queries.</summary>
    public static FileStat Of(SafeFileHandle h, string path)
    {
        if (!OperatingSystem.IsWindows())
            return new FileStat(RandomAccess.GetLength(h), File.GetLastWriteTimeUtc(path).Ticks, 0, 0);
        if (!s_noAllInfo)
        {
            var b = new byte[AllInfoBytes];
            int st = NtQueryInformationFile(h, out _, b, (uint)b.Length, FileAllInformationClass);
            if (st == 0 || st == StatusBufferOverflow) // overflow: only the name didn't fit, the fixed part is whole
                return new FileStat(BitConverter.ToInt64(b, 48), DirectoryLister.FileTimeToUtcTicks(BitConverter.ToInt64(b, 16)),
                    DirectoryLister.FileTimeToUtcTicks(BitConverter.ToInt64(b, 24)), BitConverter.ToInt64(b, 64));
            if (Unsupported(st)) s_noAllInfo = true;
        }

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
        var full = DirectoryLister.LongPath(path);
        if (ByName(full) is { } quick) return quick;
        using var h = CreateFileW(full, FileReadAttributes, 7 /*share all*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"cannot open {path} (win32 error {Marshal.GetLastWin32Error()})");
        return Of(h, path);
    }

    /// <summary>
    /// The same live values with no handle: NtQueryInformationByName(FileStatInformation) opens, queries and closes
    /// inside one call (the file's own record, as a handle sees it, never a directory entry). About half the cost
    /// of a handle on a local disk; not answered over SMB, so a volume that says it doesn't support it is not asked
    /// again. Null on any failure: the handle path then stats it, or says why it can't.
    /// </summary>
    private static FileStat? ByName(string longPath)
    {
        if (s_noByName) return null;
        var root = Path.GetPathRoot(longPath) ?? "";
        if (s_byNameUnsupported.ContainsKey(root)) return null;
        var nt = @"\??\" + longPath[4..]; // \\?\C:\x -> \??\C:\x, \\?\UNC\s\x -> \??\UNC\s\x
        if (nt.Length > 32_000) return null;
        int strBytes = Marshal.SizeOf<UnicodeString>();
        var mem = Marshal.AllocHGlobal(strBytes + nt.Length * 2);
        try
        {
            Marshal.Copy(nt.ToCharArray(), 0, mem + strBytes, nt.Length);
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(nt.Length * 2), MaximumLength = (ushort)(nt.Length * 2), Buffer = mem + strBytes }, mem, false);
            var oa = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), ObjectName = mem, Attributes = 0x40 /*OBJ_CASE_INSENSITIVE*/ };
            int st = NtQueryInformationByName(ref oa, out _, out var info, (uint)Marshal.SizeOf<FileStatInformation>(), FileStatInformationClass);
            if (st == 0)
                return new FileStat(info.EndOfFile, DirectoryLister.FileTimeToUtcTicks(info.LastWriteTime),
                    DirectoryLister.FileTimeToUtcTicks(info.ChangeTime), info.FileId);
            if (Unsupported(st)) s_byNameUnsupported[root] = true;
            return null;
        }
        catch (EntryPointNotFoundException) { s_noByName = true; return null; } // before Windows 10 1709
        finally { Marshal.FreeHGlobal(mem); }
    }

    private static bool Unsupported(int status) => (uint)status is 0xC00000BB /*NOT_SUPPORTED*/ or 0xC0000003 /*INVALID_INFO_CLASS*/
        or 0xC0000002 /*NOT_IMPLEMENTED*/ or 0xC000000D /*INVALID_PARAMETER*/;

    private static volatile bool s_noByName, s_noAllInfo;
    private static readonly ConcurrentDictionary<string, bool> s_byNameUnsupported = new(StringComparer.OrdinalIgnoreCase);
    private const int FileStatInformationClass = 68, FileAllInformationClass = 18, AllInfoBytes = 512;
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status, Information; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatInformation
    {
        public long FileId, CreationTime, LastAccessTime, LastWriteTime, ChangeTime, AllocationSize, EndOfFile;
        public uint FileAttributes, ReparseTag, NumberOfLinks, EffectiveAccess;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationByName(ref ObjectAttributes oa, out IoStatusBlock io, out FileStatInformation info, uint length, int infoClass);

    // FILE_ALL_INFORMATION: basic (times at 16/24) · standard (EndOfFile at 48) · internal (IndexNumber at 64) · ...
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle h, out IoStatusBlock io, byte[] info, uint length, int infoClass);

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
