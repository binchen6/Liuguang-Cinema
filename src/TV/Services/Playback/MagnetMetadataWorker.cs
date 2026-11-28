using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MonoTorrent;
using MonoTorrent.Client;

namespace TV.Services.Playback;

internal static class MagnetMetadataWorker
{
    internal const string Argument = "--torrent-metadata";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;

    // Never give untrusted BEP 9 metadata to the GUI process before the torrent engine
    // has checked the info hash. Bound the worker's lifetime and its committed memory.
    internal static async Task<Torrent> ReadAsync(string address, string directory, CancellationToken token)
    {
        using var job = CreateLimitedJob();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TV.exe"))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动磁力元数据进程。");
        if (!AssignProcessToJobObject(job, process.SafeHandle))
        {
            var error = Marshal.GetLastPInvokeError();
            if (!process.HasExited) process.Kill();
            throw new Win32Exception(error, "无法限制磁力元数据进程资源。");
        }
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        try
        {
            await process.StandardInput.WriteLineAsync(address.AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            process.StandardInput.Close();

            var output = process.StandardOutput.BaseStream;
            var length = new byte[sizeof(int)];
            await output.ReadExactlyAsync(length, token).ConfigureAwait(false);
            var count = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (count is <= 0 or > MaxMetadataBytes) throw new InvalidDataException("磁力元数据大小超出限制。");
            var data = new byte[count];
            await output.ReadExactlyAsync(data, token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0 || !Torrent.TryLoad(data, out var torrent))
                throw new InvalidDataException("磁力元数据未通过验证。");
            return torrent;
        }
        catch (EndOfStreamException ex)
        {
            throw new IOException("没有收到有效的磁力元数据，请检查资源的可用节点。", ex);
        }
        finally
        {
            job.Dispose();
            try { await stderr.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    internal static async Task<int> RunAsync()
    {
        try
        {
            var address = await Console.In.ReadLineAsync().ConfigureAwait(false);
            if (!MagnetPlayback.TryParse(address, out var magnet)) return 2;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            magnet = await MagnetPlayback.ResolveUdpTrackersAsync(magnet, deadline.Token).ConfigureAwait(false);
            using var engine = new ClientEngine(MagnetPlayback.Settings(Environment.CurrentDirectory), MagnetPlayback.CreateFactories());
            var data = await engine.DownloadMetadataAsync(magnet, deadline.Token).ConfigureAwait(false);
            if (data.Length is <= 0 or > MaxMetadataBytes) return 3;
            var length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
            await using var output = Console.OpenStandardOutput();
            await output.WriteAsync(length, deadline.Token).ConfigureAwait(false);
            await output.WriteAsync(data, deadline.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static SafeFileHandle CreateLimitedJob()
    {
        var job = CreateJobObject(0, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "无法创建磁力元数据进程限制。");
        }
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits
            {
                LimitFlags = 0x00000008 | 0x00000100 | 0x00002000, // one process, process memory, kill on close
                ActiveProcessLimit = 1
            },
            ProcessMemoryLimit = 384 * 1024 * 1024
        };
        if (SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;
        var code = Marshal.GetLastPInvokeError();
        job.Dispose();
        throw new Win32Exception(code, "无法设置磁力元数据进程限制。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits data, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
