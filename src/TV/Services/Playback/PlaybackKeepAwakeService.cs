using System.Runtime.InteropServices;

namespace TV.Services.Playback;

/// <summary>
/// Prevents Windows display timeout (screen off) and system sleep during active video playback.
/// Uses the modern kernel Power Availability Request API (<c>PowerCreateRequest</c> / <c>PowerSetRequest</c>)
/// which is thread-independent, works reliably with Windows 10/11 Modern Standby (S0ix), requires no admin
/// privileges, and is automatically released by the OS kernel if the process terminates unexpectedly.
/// Falls back to <c>SetThreadExecutionState</c> only if power request creation is unavailable.
/// </summary>
public sealed class PlaybackKeepAwakeService : IDisposable
{
    public const string SettingKey = "playback.preventSleep";

    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x00000001;
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private static readonly nint InvalidHandle = new(-1);

    private readonly object _sync = new();
    private readonly HashSet<string> _activeConsumers = new(StringComparer.Ordinal);
    private nint _requestHandle;
    private bool _enabled = true;
    private bool _isHeld;
    private bool _usingFallback;
    private bool _disposed;

    public bool Enabled
    {
        get { lock (_sync) return _enabled; }
        set
        {
            lock (_sync)
            {
                if (_disposed || _enabled == value) return;
                _enabled = value;
                SyncStateLocked();
            }
        }
    }

    public bool IsHeld
    {
        get { lock (_sync) return _isHeld; }
    }

    public bool UsingPowerRequest
    {
        get { lock (_sync) return _requestHandle != 0 && _requestHandle != InvalidHandle && !_usingFallback; }
    }

    public void SetActive(string consumerKey, bool active)
    {
        if (string.IsNullOrWhiteSpace(consumerKey)) return;
        lock (_sync)
        {
            if (_disposed) return;
            var changed = active
                ? _activeConsumers.Add(consumerKey)
                : _activeConsumers.Remove(consumerKey);
            if (changed) SyncStateLocked();
        }
    }

    public void ClearAll()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _activeConsumers.Clear();
            SyncStateLocked();
        }
    }

    private void SyncStateLocked()
    {
        var shouldHold = _enabled && _activeConsumers.Count > 0;
        if (shouldHold == _isHeld) return;

        if (shouldHold)
        {
            AcquireLocked();
        }
        else
        {
            ReleaseLocked();
        }
    }

    private void AcquireLocked()
    {
        try
        {
            if (_requestHandle == 0 && !_usingFallback)
            {
                var reasonPtr = Marshal.StringToHGlobalUni("流光影院正在播放视频");
                try
                {
                    var context = new ReasonContext
                    {
                        Version = PowerRequestContextVersion,
                        Flags = PowerRequestContextSimpleString,
                        SimpleReasonString = reasonPtr
                    };
                    var handle = PowerCreateRequest(ref context);
                    if (handle != 0 && handle != InvalidHandle)
                    {
                        _requestHandle = handle;
                    }
                    else
                    {
                        _usingFallback = true;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(reasonPtr);
                }
            }

            if (!_usingFallback && _requestHandle != 0 && _requestHandle != InvalidHandle)
            {
                var displayOk = PowerSetRequest(_requestHandle, PowerRequestType.DisplayRequired);
                var systemOk = PowerSetRequest(_requestHandle, PowerRequestType.SystemRequired);
                if (displayOk || systemOk)
                {
                    _isHeld = true;
                    Diagnostics.Log("KeepAwake acquired (PowerRequest Display+System)");
                    return;
                }
                _usingFallback = true;
            }

            if (SetThreadExecutionState(EsContinuous | EsDisplayRequired | EsSystemRequired) != 0)
            {
                _isHeld = true;
                Diagnostics.Log("KeepAwake acquired (SetThreadExecutionState fallback)");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"KeepAwake acquire failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    private void ReleaseLocked()
    {
        if (!_isHeld) return;
        try
        {
            if (!_usingFallback && _requestHandle != 0 && _requestHandle != InvalidHandle)
            {
                PowerClearRequest(_requestHandle, PowerRequestType.DisplayRequired);
                PowerClearRequest(_requestHandle, PowerRequestType.SystemRequired);
            }
            else
            {
                SetThreadExecutionState(EsContinuous);
            }
            Diagnostics.Log("KeepAwake released");
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"KeepAwake release failed: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _isHeld = false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _activeConsumers.Clear();
            ReleaseLocked();
            if (_requestHandle != 0 && _requestHandle != InvalidHandle)
            {
                try { CloseHandle(_requestHandle); } catch { }
                _requestHandle = 0;
            }
        }
    }

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3
    }

    // Matches WinNT REASON_CONTEXT layout on x64 (32 bytes total covering the Detailed union).
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        public nint Reserved1;
        public nint Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(nint powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(nint powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
