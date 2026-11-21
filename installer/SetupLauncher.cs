using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

internal static class SetupLauncher
{
    private const string PayloadResource = "TV.Setup.InnoPayload";
    private const string PayloadFile = "setup-payload.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        string stage = null;
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var administrator = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var elevated = new WindowsPrincipal(identity).IsInRole(administrator);
                var security = StageSecurity(identity.User, administrator, elevated);
                stage = ExtractSetup(security, elevated || identity.IsSystem);

                var setup = new ProcessStartInfo(Path.Combine(stage, PayloadFile), ForwardedArguments())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = elevated || identity.IsSystem ? stage : Environment.CurrentDirectory
                };
                setup.EnvironmentVariables["TEMP"] = stage;
                setup.EnvironmentVariables["TMP"] = stage;
                if (identity.IsSystem) setup.EnvironmentVariables["SystemTemp"] = stage;
                using (var process = Process.Start(setup))
                {
                    if (process == null) throw new InvalidOperationException("安装程序无法启动。");
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
        }
        catch (Exception error)
        {
            if (!IsFileAccessError(error) && !(error is Win32Exception) && !(error is InvalidOperationException)) throw;
            Console.Error.WriteLine(error);
            if (!Array.Exists(args, value => value.Equals("/SUPPRESSMSGBOXES", StringComparison.OrdinalIgnoreCase)))
                MessageBox(IntPtr.Zero, error.Message, "流光影院安装程序", 0x10);
            return 1;
        }
        finally
        {
            RemoveStage(stage);
        }
    }

    private static DirectorySecurity StageSecurity(SecurityIdentifier user, SecurityIdentifier administrator, bool elevated)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        AddAccess(security, administrator);
        AddAccess(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        if (elevated) security.SetOwner(administrator);
        else AddAccess(security, user);
        return security;
    }

    private static void AddAccess(DirectorySecurity security, SecurityIdentifier identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    }

    private static string ExtractSetup(DirectorySecurity security, bool machineTemp)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception last = null;
        foreach (var directory in TempDirectories(machineTemp))
        {
            if (string.IsNullOrEmpty(directory) || !Path.IsPathRooted(directory) || !tried.Add(directory)) continue;
            string stage = null;
            try
            {
                Directory.CreateDirectory(directory);
                var name = Path.Combine(directory, "tv-setup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(name, security);
                stage = name;
                using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
                {
                    if (source == null) throw new InvalidOperationException("安装包缺少内置安装程序，请重新获取安装包。");
                    using (var target = new FileStream(Path.Combine(stage, PayloadFile), FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                        source.CopyTo(target, 1024 * 1024);
                }
                return stage;
            }
            catch (Exception error)
            {
                RemoveStage(stage);
                if (!IsFileAccessError(error)) throw;
                last = error;
            }
        }
        throw new IOException("无法创建安装临时目录。请检查当前用户的目录权限与可用磁盘空间。", last);
    }

    private static IEnumerable<string> TempDirectories(bool machineTemp)
    {
        if (machineTemp)
        {
            yield return ChildDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield break;
        }

        yield return DefaultTemp();
        yield return ChildDirectory(Environment.GetEnvironmentVariable("LOCALAPPDATA"), @"TV\SetupTemp");
        yield return ChildDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TV\SetupTemp");
        yield return AppDomain.CurrentDomain.BaseDirectory;
    }

    private static string DefaultTemp()
    {
        try { return Path.GetTempPath(); }
        catch (Exception error)
        {
            if (!IsFileAccessError(error)) throw;
            return null;
        }
    }

    private static string ChildDirectory(string parent, string name)
    {
        return string.IsNullOrWhiteSpace(parent) ? null : Path.Combine(parent, name);
    }

    private static string ForwardedArguments()
    {
        // Pass the original Windows quoting to Inno, including switches such as /DIR="...".
        var command = Environment.CommandLine.TrimStart();
        int firstArgument;
        if (command.StartsWith("\"", StringComparison.Ordinal)) firstArgument = command.IndexOf('"', 1) + 1;
        else
        {
            firstArgument = 0;
            while (firstArgument < command.Length && !char.IsWhiteSpace(command[firstArgument])) firstArgument++;
        }
        return command.Substring(firstArgument).TrimStart();
    }

    private static bool IsFileAccessError(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException || error is SecurityException || error is ArgumentException || error is NotSupportedException;
    }

    private static void RemoveStage(string stage)
    {
        if (stage == null) return;
        try
        {
            File.Delete(Path.Combine(stage, PayloadFile));
            Directory.Delete(stage, false);
        }
        catch (Exception error)
        {
            if (!IsFileAccessError(error)) throw;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW", ExactSpelling = true)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
