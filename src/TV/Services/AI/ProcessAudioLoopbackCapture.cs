using System.Runtime.InteropServices;

namespace TV.Services.AI;

/// <summary>
/// Captures playback audio from the current Windows process (Windows 10 20348+ WASAPI Process Loopback)
/// with automatic fallback to default render WASAPI loopback on older Windows builds,
/// and resamples to 16 kHz, 16-bit signed mono PCM frames for DashScope Realtime ASR.
/// </summary>
internal sealed class ProcessAudioLoopbackCapture : IDisposable
{
    private const int TargetSampleRate = 16000;
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const uint AudclntStreamflagsLoopback = 0x00020000;
    private const uint AudclntStreamflagsEventCallback = 0x00040000;
    private const uint AudclntStreamflagsAutoConvertPcm = 0x80000000;
    private const uint AudclntBufferFlagsSilent = 0x2;
    private const uint WaveFormatPcm = 1;
    private const uint WaveFormatIeeeFloat = 3;
    private const uint WaveFormatExtensible = 0xFFFE;

    private readonly Action<byte[]> _onPcmChunk;
    private readonly bool _processOnly;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private bool _disposed;

    public ProcessAudioLoopbackCapture(Action<byte[]> onPcmChunk, bool processOnly = false)
    {
        _onPcmChunk = onPcmChunk;
        _processOnly = processOnly;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_captureTask is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _captureTask = Task.Factory.StartNew(() => CaptureLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void CaptureLoop(CancellationToken token)
    {
        // MTA COM initialization required for ActivateAudioInterfaceAsync & WASAPI
        var hrInit = CoInitializeEx(0, 0x0); // COINIT_MULTITHREADED
        try
        {
            IAudioClient? client = null;
            var isProcessLoopback = false;
            try
            {
                client = TryActivateProcessLoopback();
                isProcessLoopback = client is not null;
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"WASAPI process loopback unavailable, using default loopback: {ex.Message}");
            }

            if (_processOnly && client is null) return;
            client ??= ActivateDefaultRenderLoopback();
            if (client is null)
                throw new InvalidOperationException("无法初始化音频捕获设备。");

            try
            {
                WaveFormatEx fmt;
                var isFloat = false;
                uint flags;
                nint formatPtr = 0;
                var ownFormatPtr = false;

                if (isProcessLoopback)
                {
                    // Process loopback doesn't implement GetMixFormat; request standard 16kHz 16-bit mono PCM directly!
                    fmt = new WaveFormatEx
                    {
                        wFormatTag = (ushort)WaveFormatPcm,
                        nChannels = 1,
                        nSamplesPerSec = TargetSampleRate,
                        wBitsPerSample = 16,
                        nBlockAlign = 2,
                        nAvgBytesPerSec = TargetSampleRate * 2,
                        cbSize = 0
                    };
                    formatPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatEx>());
                    ownFormatPtr = true;
                    Marshal.StructureToPtr(fmt, formatPtr, false);
                    flags = AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback | AudclntStreamflagsAutoConvertPcm;
                }
                else
                {
                    Marshal.ThrowExceptionForHR(client.GetMixFormat(out formatPtr));
                    ownFormatPtr = true;
                    fmt = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
                    isFloat = DetectFloatFormat(formatPtr, fmt);
                    flags = AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback;
                }

                nint readyEvent = CreateEvent(0, false, false, null);
                try
                {
                    // 100ms buffer in 100-nanosecond units
                    var hr = client.Initialize(0 /* AUDCLNT_SHAREMODE_SHARED */, flags, 1_000_000, 0, formatPtr, 0);
                    if (hr < 0 && isProcessLoopback && !_processOnly)
                    {
                        // Some drivers reject process-loopback PCM format; cleanly fall back to default render loopback.
                        Diagnostics.Log($"Process loopback Initialize failed (0x{hr:X8}), falling back to default render loopback");
                        Marshal.ReleaseComObject(client);
                        client = ActivateDefaultRenderLoopback() ?? throw new InvalidOperationException("无法初始化默认音频捕获设备。");
                        if (ownFormatPtr && formatPtr != 0) { Marshal.FreeCoTaskMem(formatPtr); formatPtr = 0; }
                        Marshal.ThrowExceptionForHR(client.GetMixFormat(out formatPtr));
                        ownFormatPtr = true;
                        fmt = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
                        isFloat = DetectFloatFormat(formatPtr, fmt);
                        flags = AudclntStreamflagsLoopback | AudclntStreamflagsEventCallback;
                        hr = client.Initialize(0, flags, 1_000_000, 0, formatPtr, 0);
                    }
                    Marshal.ThrowExceptionForHR(hr);
                    Marshal.ThrowExceptionForHR(client.SetEventHandle(readyEvent));

                    var captureGuid = typeof(IAudioCaptureClient).GUID;
                    Marshal.ThrowExceptionForHR(client.GetService(ref captureGuid, out var captureObj));
                    var capture = (IAudioCaptureClient)captureObj;
                    try
                    {
                        Marshal.ThrowExceptionForHR(client.Start());
                        try
                        {
                            var pcmBuffer = new List<byte>(6400);
                            var srcRate = (int)fmt.nSamplesPerSec;
                            var srcChannels = Math.Max(1, (int)fmt.nChannels);
                            var srcBits = fmt.wBitsPerSample;

                            while (!token.IsCancellationRequested)
                            {
                                // WAIT_FAILED 表示事件句柄已失效，继续轮询只会空转，直接退出捕获循环
                                if (WaitForSingleObject(readyEvent, 120) == 0xFFFFFFFFu) break;
                                if (token.IsCancellationRequested) break;

                                while (capture.GetNextPacketSize(out var packetFrames) == 0 && packetFrames > 0)
                                {
                                    var getHr = capture.GetBuffer(out var dataPtr, out var frames, out var bufFlags, out _, out _);
                                    if (getHr != 0) break;
                                    try
                                    {
                                        if (frames > 0)
                                        {
                                            var silent = (bufFlags & AudclntBufferFlagsSilent) != 0;
                                            AppendResampledPcm16(pcmBuffer, dataPtr, (int)frames, srcRate, srcChannels, srcBits, isFloat, silent);
                                            // Emit ~100ms frames (3200 bytes at 16kHz 16-bit mono)
                                            while (pcmBuffer.Count >= 3200)
                                            {
                                                var chunk = pcmBuffer.GetRange(0, 3200).ToArray();
                                                pcmBuffer.RemoveRange(0, 3200);
                                                _onPcmChunk(chunk);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        capture.ReleaseBuffer(frames);
                                    }
                                }
                            }
                        }
                        finally { client.Stop(); }
                    }
                    finally { Marshal.ReleaseComObject(capture); }
                }
                finally
                {
                    if (readyEvent != 0) CloseHandle(readyEvent);
                    if (ownFormatPtr && formatPtr != 0) Marshal.FreeCoTaskMem(formatPtr);
                }
            }
            finally { Marshal.ReleaseComObject(client); }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Diagnostics.Log($"WASAPI loopback capture stopped: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            // Only uninitialize if CoInitializeEx returned S_OK (0) or S_FALSE (1), not RPC_E_CHANGED_MODE.
            if (hrInit is 0 or 1) CoUninitialize();
        }
    }

    private static IAudioClient? TryActivateProcessLoopback()
    {
        // Requires Windows 10 Build 20348+ (Windows 10 21H2 Server / Windows 11)
        if (Environment.OSVersion.Version.Build < 20348) return null;

        var loopbackParams = new AudioClientActivationParams
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)Environment.ProcessId,
            ProcessLoopbackMode = 0 // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };

        var paramSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramPtr = Marshal.AllocCoTaskMem(paramSize);
        var pvPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(loopbackParams, paramPtr, false);
            var pv = new PropVariantBlob
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)paramSize,
                pBlobData = paramPtr
            };
            Marshal.StructureToPtr(pv, pvPtr, false);

            var handler = new ActivateCompletionHandler();
            try
            {
                var iid = typeof(IAudioClient).GUID;
                var hr = ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, ref iid, pvPtr, handler, out _);
                if (hr < 0) return null;
                return handler.WaitResult(TimeSpan.FromSeconds(3));
            }
            finally
            {
                // COM 激活回调可能在等待超时后才从系统线程触发：仅在回调已运行时释放事件句柄，避免原生回调内抛 ObjectDisposedException
                if (handler.Completed) handler.Dispose();
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(pvPtr);
            Marshal.FreeCoTaskMem(paramPtr);
        }
    }

    private static IAudioClient? ActivateDefaultRenderLoopback()
    {
        var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        if (enumeratorType is null) return null;
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device));
            try
            {
                var iid = typeof(IAudioClient).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, 0, out var obj));
                return (IAudioClient)obj;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static bool DetectFloatFormat(nint formatPtr, WaveFormatEx fmt)
    {
        if (fmt.wFormatTag == WaveFormatIeeeFloat) return true;
        if (fmt.wFormatTag == WaveFormatExtensible && fmt.cbSize >= 22)
        {
            var subFormat = Marshal.PtrToStructure<Guid>(formatPtr + 24);
            return subFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        }
        return false;
    }

    private static unsafe void AppendResampledPcm16(
        List<byte> target, nint dataPtr, int frames, int srcRate, int srcChannels, int srcBits, bool isFloat, bool silent)
    {
        if (srcRate <= 0 || frames <= 0) return;

        // Fast path: already 16kHz 16-bit mono PCM
        if (srcRate == TargetSampleRate && srcChannels == 1 && srcBits == 16 && !isFloat)
        {
            var byteCount = frames * 2;
            if (silent)
            {
                for (var i = 0; i < byteCount; i++) target.Add(0);
                return;
            }
            var span = new ReadOnlySpan<byte>((void*)dataPtr, byteCount);
            for (var i = 0; i < span.Length; i++) target.Add(span[i]);
            return;
        }

        // Convert to mono float [-1, 1] and linearly resample to 16kHz int16
        var outFrames = Math.Max(1, (int)Math.Round((double)frames * TargetSampleRate / srcRate));
        if (silent)
        {
            for (var i = 0; i < outFrames * 2; i++) target.Add(0);
            return;
        }

        var ratio = (double)frames / outFrames;
        for (var o = 0; o < outFrames; o++)
        {
            var srcPos = o * ratio;
            var idx0 = Math.Min(frames - 1, (int)srcPos);
            var idx1 = Math.Min(frames - 1, idx0 + 1);
            var frac = (float)(srcPos - idx0);
            var s0 = ReadMonoSample(dataPtr, idx0, srcChannels, srcBits, isFloat);
            var s1 = ReadMonoSample(dataPtr, idx1, srcChannels, srcBits, isFloat);
            var sample = Math.Clamp(s0 + (s1 - s0) * frac, -1f, 1f);
            var pcm16 = (short)Math.Round(sample * 32767f);
            target.Add((byte)(pcm16 & 0xFF));
            target.Add((byte)((pcm16 >> 8) & 0xFF));
        }
    }

    private static unsafe float ReadMonoSample(nint ptr, int frameIndex, int channels, int bits, bool isFloat)
    {
        var sum = 0f;
        if (isFloat && bits == 32)
        {
            var samples = (float*)ptr + frameIndex * channels;
            for (var c = 0; c < channels; c++) sum += samples[c];
            return sum / channels;
        }
        if (bits == 16)
        {
            var samples = (short*)ptr + frameIndex * channels;
            for (var c = 0; c < channels; c++) sum += samples[c] / 32768f;
            return sum / channels;
        }
        if (bits == 24)
        {
            var raw = (byte*)ptr + frameIndex * channels * 3;
            for (var c = 0; c < channels; c++)
            {
                var off = c * 3;
                var val = (raw[off] | (raw[off + 1] << 8) | (raw[off + 2] << 16));
                if ((val & 0x800000) != 0) val |= unchecked((int)0xFF000000);
                sum += val / 8388608f;
            }
            return sum / channels;
        }
        if (bits == 32)
        {
            var samples = (int*)ptr + frameIndex * channels;
            for (var c = 0; c < channels; c++) sum += samples[c] / 2147483648f;
            return sum / channels;
        }
        return 0f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _captureTask?.Wait(TimeSpan.FromSeconds(1.5)); } catch { }
        _cts?.Dispose();
    }

    // --- Native Interop Definitions ---

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public uint cbSize;
        public nint pBlobData;
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    private sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler, IDisposable
    {
        private readonly ManualResetEventSlim _done = new(false);
        private int _completed;
        private IAudioClient? _result;

        public bool Completed => Volatile.Read(ref _completed) == 1;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                if (activateOperation.GetActivateResult(out var hr, out var iface) == 0 && hr >= 0)
                    _result = iface as IAudioClient;
            }
            catch { }
            finally
            {
                Volatile.Write(ref _completed, 1);
                try { _done.Set(); }
                catch (ObjectDisposedException) { }
            }
            return 0;
        }

        public IAudioClient? WaitResult(TimeSpan timeout)
        {
            _done.Wait(timeout);
            return _result;
        }

        public void Dispose() => _done.Dispose();
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, uint dwStateMask, out nint ppDevices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, nint pFormat, nint audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint pFormat, out nint closestMatch);
        [PreserveSig] int GetMixFormat(out nint ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint ppData, out uint numFramesToRead, out uint dwFlags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [DllImport("mmdevapi.dll", CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint pvReserved, uint dwCoInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateEvent(nint attrs, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
