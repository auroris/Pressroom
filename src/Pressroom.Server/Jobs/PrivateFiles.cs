using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Pressroom.Server.Jobs;

internal static class PrivateFiles
{
    public static void Directory(string path)
    {
        if (System.IO.Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Job storage cannot use symbolic links or junctions.");
        if (OperatingSystem.IsWindows()) WindowsDirectory(path);
        else
        {
            System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(identity.User!);
        acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (directory.Exists) directory.SetAccessControl(acl);
        else directory.Create(acl);
    }

    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    public static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        var fd = Open(path, 0x10000 /* O_DIRECTORY */);
        if (fd < 0) throw new IOException("Could not open job directory for durability.");
        try { if (Fsync(fd) != 0) throw new IOException("Could not flush job directory."); }
        finally { Close(fd); }
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int fd);
}
