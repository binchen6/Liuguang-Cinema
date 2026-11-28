using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TV.Services.Spider;

// The signed Java 17 Windows launcher uses the system ANSI code page for parts of startup.
internal static class JavaWindowsPaths
{
    private static readonly object Sync = new();
    private static (string Executable, string Jar)? _files;

    internal static bool NeedsStaging(string executable, string jar) =>
        !CanLaunchFrom(executable) || !CanLaunchFrom(jar);

    internal static (string Executable, string Jar) Stage(string executable, string jar)
    {
        lock (Sync)
        {
            if (_files is { } previous && File.Exists(previous.Executable) && File.Exists(previous.Jar)) return previous;
            var staging = CreatePrivateDirectory();
            try
            {
                var stagedJar = Path.Combine(staging.FullName, "catvod-engine.jar");
                File.Copy(jar, stagedJar);
                if (!CanLaunchFrom(executable))
                {
                    var runtime = Directory.GetParent(Path.GetDirectoryName(executable)!)!.FullName;
                    var stagedRuntime = Directory.CreateDirectory(Path.Combine(staging.FullName, "java")).FullName;
                    foreach (var file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
                    {
                        var target = Path.Combine(stagedRuntime, Path.GetRelativePath(runtime, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target);
                    }
                    executable = Path.Combine(stagedRuntime, "bin", "java.exe");
                }
                AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveStaging(staging);
                return (_files = (executable, stagedJar)).Value;
            }
            catch
            {
                RemoveStaging(staging);
                throw;
            }
        }
    }

    private static DirectoryInfo CreatePrivateDirectory()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("无法确定 Java 运行时缓存的所有者。");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var root in new[] { Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root) || !CanLaunchFrom(root)) continue;
            var directory = new DirectoryInfo(Path.Combine(root, "LiuguangJava-" + Guid.NewGuid().ToString("N")));
            try
            {
                directory.Create(acl);
                return directory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        throw new NotSupportedException("当前 Windows 代码页无法读取 Java 路径，且无法创建运行时缓存。请将流光影院移到普通英文路径。");
    }

    private static void RemoveStaging(DirectoryInfo staging)
    {
        try
        {
            if (staging.Exists && staging.Name.StartsWith("LiuguangJava-", StringComparison.Ordinal) &&
                (staging.Attributes & FileAttributes.ReparsePoint) == 0)
                staging.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool CanLaunchFrom(string path)
    {
        if (path.Length >= 245) return false;
        if (path.All(char.IsAscii)) return true;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding((int)GetACP(), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try { return string.Equals(path, encoding.GetString(encoding.GetBytes(path)), StringComparison.Ordinal); }
        catch (EncoderFallbackException) { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
