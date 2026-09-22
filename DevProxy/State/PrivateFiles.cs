using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DevProxy.State;

internal static class PrivateFiles
{
    public static void EnsureDirectory(string directory, bool secureExisting = false)
    {
        if (Path.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Private Dev Proxy directories must not be symbolic links.");
        }

        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsDirectory(directory, secureExisting);
        }
        else
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            _ = Directory.CreateDirectory(directory, mode);
            if (secureExisting)
            {
                File.SetUnixFileMode(directory, mode);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsDirectory(string directory, bool secureExisting)
    {
        var exists = Directory.Exists(directory);
        if (exists && !secureExisting)
        {
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Unable to identify the current user.");
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        var info = new DirectoryInfo(directory);
        if (!exists)
        {
            info.Create(security);
        }
        else
        {
            const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
            if (info.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections) != security.GetSecurityDescriptorSddlForm(sections))
            {
                info.SetAccessControl(security);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindowsFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Unable to identify the current user.");
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.None, 4096, FileOptions.Asynchronous, security);
    }

    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var stagingPath = $"{path}.{Guid.NewGuid():N}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = OperatingSystem.IsWindows() ? CreateWindowsFile(stagingPath) : new FileStream(stagingPath, options))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(contents.AsMemory(), cancellationToken);
            }

            File.Move(stagingPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(stagingPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }
}