using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using TV.Models;

namespace TV.Services.Playback;

public sealed record PlaybackTrack(int Id, string? Name);
internal sealed record PlaybackTrackSelection(PlaybackTrack? Audio, PlaybackTrack? Subtitle, bool Paused = false, float Rate = 1);
public sealed record PlayerSnapshot(VLCState State, bool IsPlaying, long TimeMs, long LengthMs, bool CanSeek,
    float BufferingPercent, IReadOnlyList<PlaybackTrack> AudioTracks, IReadOnlyList<PlaybackTrack> SubtitleTracks,
    int AudioTrack, int SubtitleTrack, VideoFormat? VideoFormat, float Rate,
    long CapturedAtTimestamp = 0, string MediaInfo = "");

public sealed class PlayerService : IDisposable
{
    private static readonly int DecoderThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private readonly object _stopSync = new();
    private readonly object _commandSync = new();
    private readonly object _coreSync = new();
    private Task _commandTail = Task.CompletedTask;
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private long _pendingStartPosition;
    private long? _pendingSeekPosition;
    private bool _positionApplied;
    private bool _disposed;
    private string[]? _swapChainOptions;
    private nint _swapChain;
    private nint _deviceContext;
    private PlaybackTrackSelection? _pendingTracks;
    private int _trackAttempts;
    private Task? _stopTask;
    private int _stopping;
    private VideoFormat? _videoFormat;
    private string _mediaInfo = string.Empty;
    private bool _videoOutputReady;
    private int _snapshotReads;
    private int _volume = 85;
    private string? _aspectRatio;
    private string? _cropGeometry;
    private PlayerSnapshot _snapshot = new(VLCState.NothingSpecial, false, 0, 0, false, 100, [], [], -1, -1, null, 1);

    private readonly object _controlSync = new();
    private int _controlPumpScheduled;
    private int _pendingPauseState = -1; // -1 none, 0 play, 1 pause
    private long _pendingImmediateSeekMs = -1;
    private int _pendingVolume = -1;
    private float _pendingRate = -1f;
    private long _pendingSubtitleDelayMs = long.MinValue;
    private long _pendingAudioDelayMs = long.MinValue;
    private bool _pendingGeometryUpdate;

    private static readonly object VlcInitSync = new();
    private static volatile bool _vlcRuntimeInitialized;

    public PlayerService()
    {
        // Defer LibVLC native initialization until EnsureInitializedAsync(swapChainOptions)
        // or first headless access, eliminating the redundant double-create startup cost & memory footprint.
    }

    internal static void EnsureVlcRuntimeInitialized()
    {
        if (_vlcRuntimeInitialized) return;
        lock (VlcInitSync)
        {
            if (_vlcRuntimeInitialized) return;
            // Always prefer the bundled libvlc runtime over any system-installed VLC to guarantee version compatibility across PCs.
            var bundledDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
            if (Directory.Exists(bundledDir) && File.Exists(Path.Combine(bundledDir, "libvlc.dll")))
                Core.Initialize(bundledDir);
            else
                Core.Initialize();
            _vlcRuntimeInitialized = true;
        }
    }

    private (LibVLC libVLC, MediaPlayer player) CreateCore(string[]? swapChainOptions)
    {
        EnsureVlcRuntimeInitialized();
        var options = new List<string>
        {
            $"--network-caching={(App.PlayerOnly ? 1000 : NetworkCachingMs)}",
            $"--live-caching={(App.PlayerOnly ? 800 : LiveCachingMs)}",
            "--file-caching=300",
            "--disc-caching=300",
            "--no-osd",
            "--no-video-title-show",
            "--no-stats",
            "--no-snapshot-preview",
            "--no-metadata-network-access",
            "--no-lua"
        };
        if (swapChainOptions is not null) options.AddRange(swapChainOptions);
        var libVLC = new LibVLC(options.ToArray());
        if (HasHdrSurface) libVLC.Log += OnNativeVideoLog;
        var player = new MediaPlayer(libVLC) { EnableHardwareDecoding = HardwareDecoding, Volume = _volume };
        return (libVLC, player);
    }

    private MediaPlayer EnsurePlayerCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mediaPlayer is not null) return _mediaPlayer;
        lock (_coreSync)
        {
            if (_mediaPlayer is not null) return _mediaPlayer;
            (_libVLC, _mediaPlayer) = CreateCore(null);
            return _mediaPlayer;
        }
    }

    public async Task EnsureInitializedAsync(string[] swapChainOptions)
    {
        if (_disposed || _swapChainOptions is not null && _swapChainOptions.SequenceEqual(swapChainOptions)) return;
        _videoOutputReady = false;
        var hdrDisplay = VideoDisplay.IsHdrActive(App.Window.AppWindow.Id);
        var swapChain = VideoDisplay.RetainNativeReference(swapChainOptions, "winrt-swapchain");
        nint deviceContext = 0;
        try
        {
            deviceContext = VideoDisplay.RetainNativeReference(swapChainOptions, "winrt-d3dcontext");
            Interlocked.Exchange(ref _stopping, 1);
            await RunNativeAsync(() =>
            {
                StopCore();
                _mediaPlayer?.Dispose();
                _libVLC?.Dispose();
                _mediaPlayer = null;
                _libVLC = null;
                ReleaseVideoOutput();
                HasHdrSurface = hdrDisplay && VideoDisplay.TryEnableHdrOutput(swapChain);
                (_libVLC, _mediaPlayer) = CreateCore(swapChainOptions);
                _swapChainOptions = swapChainOptions;
                _swapChain = swapChain;
                _deviceContext = deviceContext;
                swapChain = deviceContext = 0;
                _mediaPlayer.EnableHardwareDecoding = HardwareDecoding;
                ApplyGeometryCore(_mediaPlayer);
            }, CancellationToken.None).ConfigureAwait(false);
            _videoOutputReady = true;
            Diagnostics.Log($"LibVLC initialized with VideoView swapchain, HDR surface={HasHdrSurface}");
        }
        finally
        {
            Interlocked.Exchange(ref _stopping, 0);
            if (swapChain != 0) Marshal.Release(swapChain);
            if (deviceContext != 0) Marshal.Release(deviceContext);
        }
    }

    private static void OnNativeVideoLog(object? sender, LogEventArgs e)
    {
        if (e.Module == "direct3d11" && e.Message.StartsWith("using colorspace ", StringComparison.Ordinal))
            Diagnostics.Log("LibVLC output " + e.Message);
    }

    private void ReleaseVideoOutput()
    {
        if (_swapChain != 0) Marshal.Release(_swapChain);
        if (_deviceContext != 0) Marshal.Release(_deviceContext);
        _swapChain = _deviceContext = 0;
        _swapChainOptions = null;
    }

    public LibVLC LibVLC
    {
        get
        {
            EnsurePlayerCore();
            return _libVLC!;
        }
    }

    public MediaPlayer MediaPlayer => EnsurePlayerCore();
    public PlaybackRequest? CurrentRequest { get; private set; }
    public float BufferingPercent { get; private set; } = 100;
    public int NetworkCachingMs { get; set; } = 3600;
    public int LiveCachingMs { get; set; } = 1800;
    public bool HardwareDecoding { get; set; } = true;
    internal bool HasHdrSurface { get; private set; }
    internal bool IsVideoOutputReady => _videoOutputReady;
    public bool IsStopping => _stopping != 0;
    public PlayerSnapshot LastSnapshot => _snapshot;
    public event EventHandler<float>? BufferingChanged;
    public event EventHandler? PlaybackChanged;

    public Task PlayAsync(PlaybackRequest request, CancellationToken cancellationToken = default) => PlayAsync(request, null, cancellationToken);

    internal Task PlayAsync(PlaybackRequest request, PlaybackTrackSelection? tracks, CancellationToken cancellationToken) =>
        RunNativeAsync(() => PlayCore(request, tracks), cancellationToken);

    private void PlayCore(PlaybackRequest request, PlaybackTrackSelection? tracks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var player = EnsurePlayerCore();
        var libVLC = _libVLC!;
        Interlocked.Exchange(ref _stopping, 1);
        try { StopCore(); }
        finally { Interlocked.Exchange(ref _stopping, 0); }
        CurrentRequest = request;
        _pendingStartPosition = request.StartPositionMs;
        _pendingSeekPosition = null;
        _positionApplied = request.StartPositionMs <= 0;
        _pendingTracks = tracks;
        _trackAttempts = 0;
        _snapshotReads = 0;
        _videoFormat = null;
        _mediaInfo = string.Empty;
        _snapshot = new(VLCState.Opening, false, request.StartPositionMs, 0, false, 0, [], [], -1, -1, null, tracks?.Rate ?? 1, Stopwatch.GetTimestamp(), string.Empty);
        var media = new Media(libVLC, request.Url, FromType.FromLocation);
        _currentMedia = media;
        var isLocalFile = Uri.TryCreate(request.Url, UriKind.Absolute, out var parsed) && parsed.IsFile;
        try
        {
            player.EnableHardwareDecoding = HardwareDecoding;
            media.AddOption(HardwareDecoding ? ":avcodec-hw=d3d11va" : ":avcodec-hw=none");
            // Cap avcodec decode threads: hardware decode needs minimal frame buffers (2), software decode bounded (2..6).
            var decodeThreads = HardwareDecoding ? 2 : DecoderThreads;
            media.AddOption($":avcodec-threads={decodeThreads}");
            if (isLocalFile)
            {
                media.AddOption(":file-caching=300");
            }
            else
            {
                media.AddOption(request.Kind == PlaybackKind.Live ? $":live-caching={LiveCachingMs}" : $":network-caching={NetworkCachingMs}");
                media.AddOption(":file-caching=500");
                media.AddOption(":http-reconnect=true");
                media.AddOption(":http-forward-cookies=true");
                media.AddOption(":adaptive-use-access=true");
            }
            if (request.UseAvformatDemuxer) media.AddOption(":demux=avformat");
            foreach (var subtitle in request.Subtitles) media.AddSlave(MediaSlaveType.Subtitle, 3, subtitle);
            foreach (var audio in request.AudioTracks) media.AddSlave(MediaSlaveType.Audio, 3, audio);
            foreach (var (key, value) in request.Headers)
            {
                if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) media.AddOption($":http-user-agent={value}");
                else if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || key.Equals("Referrer", StringComparison.OrdinalIgnoreCase)) media.AddOption($":http-referrer={value}");
            }
            player.Buffering += OnBuffering;
            player.EndReached += OnPlaybackChanged;
            player.EncounteredError += OnPlaybackChanged;
            ApplyGeometryCore(player);
            if (!player.Play(media)) throw new InvalidOperationException("播放器无法启动该媒体，请尝试其他线路。");
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            StopCore();
            throw;
        }
    }

    public void TogglePause() => SetPause(_snapshot.IsPlaying);

    /// <summary>
    /// Deterministic, high-priority pause/resume command that coalesces past queued volume/slider ticks
    /// and updates LastSnapshot immediately so UI & danmaku respond with minimal latency.
    /// </summary>
    public void SetPause(bool paused)
    {
        lock (_controlSync) _pendingPauseState = paused ? 1 : 0;
        ScheduleControlPump();
    }

    internal int CurrentVideoHeight => _videoFormat?.Height ?? 0;

    public Task<PlayerSnapshot> ReadSnapshotAsync(CancellationToken token) => RunNativeCoreAsync(() =>
    {
        if (_mediaPlayer is null) return _snapshot;
        ApplyPendingPositionCore();
        ApplyPendingTracksCore();
        var state = _mediaPlayer.State;
        var video = ReadVideoFormatCore(state);
        var readTracks = ++_snapshotReads <= 4 || _snapshotReads % 20 == 0;
        var audio = readTracks ? DescribeTracks(_mediaPlayer.AudioTrackDescription) : _snapshot.AudioTracks;
        var subtitles = readTracks ? DescribeTracks(_mediaPlayer.SpuDescription) : _snapshot.SubtitleTracks;
        _snapshot = new PlayerSnapshot(state, _mediaPlayer.IsPlaying, Math.Max(0, _mediaPlayer.Time), Math.Max(0, _mediaPlayer.Length),
            _mediaPlayer.IsSeekable, BufferingPercent, audio, subtitles, _mediaPlayer.AudioTrack, _mediaPlayer.Spu, video, _mediaPlayer.Rate,
            Stopwatch.GetTimestamp(), _mediaInfo);
        return _snapshot;
    }, token);

    private VideoFormat? ReadVideoFormatCore(VLCState state)
    {
        if (_videoFormat is not null || _currentMedia is null || _mediaPlayer is null || state is not (VLCState.Playing or VLCState.Paused)) return _videoFormat;
        uint width = 0, height = 0;
        var allTracks = _currentMedia.Tracks;
        var videoTracks = allTracks.Where(track => track.TrackType == TrackType.Video && track.Data.Video.Height > 0).ToArray();
        if (videoTracks.Length == 0)
        {
            if (_mediaPlayer.Size(0, ref width, ref height) && height > 0)
            {
                _videoFormat = new VideoFormat((int)height, 0);
                _mediaInfo = $"{width}×{height} · {(HardwareDecoding ? "D3D11 硬解" : "软解")}";
            }
            return _videoFormat;
        }
        var track = videoTracks.FirstOrDefault(track => track.Id == _mediaPlayer.VideoTrack);
        if (track.Data.Video.Height == 0) track = videoTracks[0];
        var video = track.Data.Video;
        var fps = video.FrameRateDen == 0 ? 0d : (double)video.FrameRateNum / video.FrameRateDen;
        var frameRate = VideoFormat.HighFrameRate(fps);
        _videoFormat = new VideoFormat((int)video.Height, frameRate);

        var vCodec = FormatFourCc(track.Codec);
        var audioTrack = allTracks.FirstOrDefault(t => t.TrackType == TrackType.Audio);
        var aCodec = audioTrack.TrackType == TrackType.Audio ? FormatFourCc(audioTrack.Codec) : string.Empty;
        var parts = new List<string> { $"{video.Width}×{video.Height}" };
        if (fps is > 1 and < 240) parts.Add($"{fps:0.##} fps");
        if (vCodec.Length > 0) parts.Add(vCodec);
        if (aCodec.Length > 0) parts.Add(aCodec);
        parts.Add(HardwareDecoding ? "D3D11 硬解" : "软解");
        if (HasHdrSurface) parts.Add("HDR");
        _mediaInfo = string.Join(" · ", parts);
        return _videoFormat;
    }

    private static string FormatFourCc(uint fourcc)
    {
        if (fourcc == 0) return string.Empty;
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var b = (byte)((fourcc >> (i * 8)) & 0xFF);
            if (b < 0x20 || b > 0x7E) return string.Empty;
            chars[i] = char.ToUpperInvariant((char)b);
        }
        return new string(chars).Trim();
    }

    public void Seek(long positionMs)
    {
        lock (_controlSync) _pendingImmediateSeekMs = Math.Max(0, positionMs);
        ScheduleControlPump();
    }

    private void ClearPendingControls()
    {
        lock (_controlSync)
        {
            _pendingPauseState = -1;
            _pendingImmediateSeekMs = -1;
            _pendingVolume = -1;
            _pendingRate = -1f;
            _pendingSubtitleDelayMs = long.MinValue;
            _pendingAudioDelayMs = long.MinValue;
            _pendingGeometryUpdate = false;
        }
    }

    private void ScheduleControlPump()
    {
        if (_disposed || IsStopping) return;
        if (Interlocked.CompareExchange(ref _controlPumpScheduled, 1, 0) != 0) return;
        _ = Task.Run(DrainControlsLoopAsync);
    }

    private async Task DrainControlsLoopAsync()
    {
        try
        {
            while (!_disposed && !IsStopping)
            {
                int pauseState;
                long seekMs;
                int volume;
                float rate;
                long subDelay;
                long audDelay;
                bool geometry;
                lock (_controlSync)
                {
                    pauseState = _pendingPauseState;
                    seekMs = _pendingImmediateSeekMs;
                    volume = _pendingVolume;
                    rate = _pendingRate;
                    subDelay = _pendingSubtitleDelayMs;
                    audDelay = _pendingAudioDelayMs;
                    geometry = _pendingGeometryUpdate;

                    _pendingPauseState = -1;
                    _pendingImmediateSeekMs = -1;
                    _pendingVolume = -1;
                    _pendingRate = -1f;
                    _pendingSubtitleDelayMs = long.MinValue;
                    _pendingAudioDelayMs = long.MinValue;
                    _pendingGeometryUpdate = false;
                }

                if (pauseState < 0 && seekMs < 0 && volume < 0 && rate <= 0f &&
                    subDelay == long.MinValue && audDelay == long.MinValue && !geometry)
                {
                    break;
                }

                await _nativeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_disposed || IsStopping || _mediaPlayer is null) break;

                    // 1. Highest priority: deterministic play / pause
                    if (pauseState >= 0 && _currentMedia is not null)
                    {
                        var shouldPause = pauseState == 1;
                        if (shouldPause)
                        {
                            _mediaPlayer.SetPause(true);
                        }
                        else
                        {
                            if (_mediaPlayer.State == VLCState.Paused) _mediaPlayer.SetPause(false);
                            else _mediaPlayer.Play();
                        }
                        _snapshot = _snapshot with
                        {
                            IsPlaying = !shouldPause,
                            State = shouldPause ? VLCState.Paused : VLCState.Playing,
                            CapturedAtTimestamp = Stopwatch.GetTimestamp()
                        };
                        PlaybackChanged?.Invoke(this, EventArgs.Empty);
                    }

                    // 2. Seek
                    if (seekMs >= 0)
                    {
                        if (_mediaPlayer.Length > 0 && _mediaPlayer.IsSeekable)
                        {
                            _pendingSeekPosition = null;
                            var target = Math.Clamp(seekMs, 0, _mediaPlayer.Length);
                            _mediaPlayer.Time = target;
                            _positionApplied = true;
                            _snapshot = _snapshot with { TimeMs = target, CapturedAtTimestamp = Stopwatch.GetTimestamp() };
                        }
                        else if (CurrentRequest?.Kind == PlaybackKind.Vod)
                        {
                            _pendingSeekPosition = seekMs;
                        }
                    }

                    // 3. Continuous / secondary controls
                    if (rate > 0f)
                    {
                        _mediaPlayer.SetRate(rate);
                        _snapshot = _snapshot with { Rate = rate };
                    }
                    if (volume >= 0) _mediaPlayer.Volume = volume;
                    if (subDelay != long.MinValue) _mediaPlayer.SetSpuDelay(subDelay * 1000L);
                    if (audDelay != long.MinValue) _mediaPlayer.SetAudioDelay(audDelay * 1000L);
                    if (geometry) ApplyGeometryCore(_mediaPlayer);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log($"LibVLC control failed: {ex.GetType().Name} {ex.Message}");
                }
                finally
                {
                    _nativeGate.Release();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _controlPumpScheduled, 0);
            // Re-check once after releasing flag so a concurrent enqueue is never lost.
            bool hasMore;
            lock (_controlSync)
            {
                hasMore = _pendingPauseState >= 0 || _pendingImmediateSeekMs >= 0 || _pendingVolume >= 0 ||
                    _pendingRate > 0f || _pendingSubtitleDelayMs != long.MinValue ||
                    _pendingAudioDelayMs != long.MinValue || _pendingGeometryUpdate;
            }
            if (hasMore && !_disposed && !IsStopping) ScheduleControlPump();
        }
    }

    private void StopCore()
    {
        ClearPendingControls();
        if (_currentMedia is null) return;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Buffering -= OnBuffering;
            _mediaPlayer.EndReached -= OnPlaybackChanged;
            _mediaPlayer.EncounteredError -= OnPlaybackChanged;
        }
        var current = _currentMedia;
        try { _mediaPlayer?.Stop(); }
        finally
        {
            current.Dispose();
            _currentMedia = null;
            _pendingSeekPosition = null;
            _pendingTracks = null;
            _videoFormat = null;
            _mediaInfo = string.Empty;
            BufferingPercent = 100;
            _snapshot = new(VLCState.Stopped, false, _snapshot.TimeMs, _snapshot.LengthMs, false, 100,
                [], [], -1, -1, null, _snapshot.Rate, Stopwatch.GetTimestamp(), string.Empty);
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task StopAsync()
    {
        lock (_stopSync)
        {
            if (_stopTask is { IsCompleted: false } pending) return pending;
            Interlocked.Exchange(ref _stopping, 1);
            var task = RunNativeAsync(StopCore, CancellationToken.None);
            _stopTask = ClearStoppingAsync(task);
            return _stopTask;
        }
    }

    private async Task ClearStoppingAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _stopping, 0); }
    }

    private void ApplyPendingPositionCore()
    {
        if (_mediaPlayer is null || _positionApplied && !_pendingSeekPosition.HasValue) return;
        if (_mediaPlayer.Length <= 0 || !_mediaPlayer.IsSeekable) return;
        if (_pendingSeekPosition is { } seek)
        {
            _mediaPlayer.Time = Math.Clamp(seek, 0, _mediaPlayer.Length);
            _pendingSeekPosition = null;
            _positionApplied = true;
        }
        else if (!_positionApplied && _pendingStartPosition > 0)
        {
            _mediaPlayer.Time = Math.Min(_pendingStartPosition, Math.Max(0, _mediaPlayer.Length - 3000));
            _positionApplied = true;
        }
    }

    internal Task<PlaybackTrackSelection> CaptureTrackSelectionAsync(CancellationToken token) => RunNativeAsync(() =>
    {
        if (_mediaPlayer is null) return new PlaybackTrackSelection(null, null, false, 1);
        var audio = DescribeTrack(_mediaPlayer.AudioTrackDescription, _mediaPlayer.AudioTrack);
        var subtitle = DescribeTrack(_mediaPlayer.SpuDescription, _mediaPlayer.Spu);
        return new PlaybackTrackSelection(audio, subtitle, _mediaPlayer.State == VLCState.Paused, _mediaPlayer.Rate);
    }, token);

    private void ApplyPendingTracksCore()
    {
        if (_mediaPlayer is null || _pendingTracks is not { } tracks || _mediaPlayer.State is not (VLCState.Playing or VLCState.Paused)) return;
        if (_trackAttempts == 0)
        {
            if (tracks.Rate > 0 && tracks.Rate != 1) _mediaPlayer.SetRate(tracks.Rate);
            if (tracks.Paused) _mediaPlayer.SetPause(true);
        }
        var audio = tracks.Audio is { } a && !RestoreTrack(a, _mediaPlayer.AudioTrackDescription, SelectAudioTrackCore) ? a : null;
        var subtitle = tracks.Subtitle is { } s && !RestoreTrack(s, _mediaPlayer.SpuDescription, _mediaPlayer.SetSpu) ? s : null;
        _pendingTracks = ++_trackAttempts >= 8 || audio is null && subtitle is null ? null : tracks with { Audio = audio, Subtitle = subtitle };
    }

    internal Task<bool> SelectTrackAsync(bool subtitle, int id) => RunNativeAsync(() =>
    {
        if (_disposed || _mediaPlayer is null) return false;
        if (_pendingTracks is { } tracks) _pendingTracks = subtitle ? tracks with { Subtitle = null } : tracks with { Audio = null };
        return subtitle ? _mediaPlayer.SetSpu(id) : SelectAudioTrackCore(id);
    }, CancellationToken.None);

    private bool SelectAudioTrackCore(int id)
    {
        if (_mediaPlayer is null) return false;
        if (_mediaPlayer.AudioTrack == id) return true;
        var position = _mediaPlayer.Time;
        if (!_mediaPlayer.SetAudioTrack(id)) return false;
        if (id >= 0 && position >= 0 && CurrentRequest?.SeekOnAudioTrackChange == true && _mediaPlayer.IsSeekable) _mediaPlayer.Time = position;
        return true;
    }

    internal Task<bool> AddSubtitleAsync(Uri file) => RunNativeAsync(() =>
        _mediaPlayer is not null && _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, file.AbsoluteUri, true), CancellationToken.None);

    internal void SetRate(float rate)
    {
        if (rate <= 0f || !float.IsFinite(rate)) return;
        lock (_controlSync) _pendingRate = rate;
        ScheduleControlPump();
    }

    internal void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        lock (_controlSync) _pendingVolume = _volume;
        ScheduleControlPump();
    }

    internal void SetBufferingPause(bool paused) => SetPause(paused);

    internal void SetVideoGeometry(string? aspectRatio, string? cropGeometry)
    {
        _aspectRatio = aspectRatio;
        _cropGeometry = cropGeometry;
        lock (_controlSync) _pendingGeometryUpdate = true;
        ScheduleControlPump();
    }

    private void ApplyGeometryCore(MediaPlayer player)
    {
        player.AspectRatio = _aspectRatio;
        player.CropGeometry = _cropGeometry;
    }

    internal void SetSubtitleDelayMs(long delayMs)
    {
        lock (_controlSync) _pendingSubtitleDelayMs = delayMs;
        ScheduleControlPump();
    }

    internal void SetAudioDelayMs(long delayMs)
    {
        lock (_controlSync) _pendingAudioDelayMs = delayMs;
        ScheduleControlPump();
    }
    internal void NextFrame() => RunFireAndForget(() => _mediaPlayer?.NextFrame());
    internal Task<bool> TakeSnapshotAsync(string filePath, CancellationToken token = default) => RunNativeAsync(() =>
        _mediaPlayer is not null && _currentMedia is not null && _mediaPlayer.TakeSnapshot(0, filePath, 0, 0), token);

    private static PlaybackTrack[] DescribeTracks(IEnumerable<TrackDescription> descriptions) =>
        descriptions.Where(track => track.Id >= 0).Select(track => new PlaybackTrack(track.Id, track.Name)).ToArray();

    private static PlaybackTrack? DescribeTrack(TrackDescription[] descriptions, int id)
    {
        if (id < 0) return descriptions.Any(track => track.Id >= 0) ? new PlaybackTrack(-1, null) : null;
        var track = descriptions.FirstOrDefault(track => track.Id == id);
        return new PlaybackTrack(id, track.Name);
    }

    private static bool RestoreTrack(PlaybackTrack previous, TrackDescription[] descriptions, Func<int, bool> select)
    {
        if (!descriptions.Any(track => track.Id >= 0)) return false;
        if (previous.Id < 0) return select(-1);
        if (string.IsNullOrWhiteSpace(previous.Name)) return descriptions.Any(track => track.Id == previous.Id) && select(previous.Id);
        var named = descriptions.Where(track => track.Id >= 0 && string.Equals(previous.Name, track.Name, StringComparison.Ordinal))
            .OrderByDescending(track => track.Id == previous.Id).Select(track => (int?)track.Id).FirstOrDefault();
        return named is { } id && select(id);
    }

    private async Task RunNativeAsync(Action action, CancellationToken token)
    {
        Task pending;
        lock (_commandSync) pending = _commandTail;
        await pending.WaitAsync(token).ConfigureAwait(false);
        await RunNativeCoreAsync(action, token).ConfigureAwait(false);
    }

    private async Task<T> RunNativeAsync<T>(Func<T> action, CancellationToken token)
    {
        Task pending;
        lock (_commandSync) pending = _commandTail;
        await pending.WaitAsync(token).ConfigureAwait(false);
        return await RunNativeCoreAsync(action, token).ConfigureAwait(false);
    }

    private async Task RunNativeCoreAsync(Action action, CancellationToken token) =>
        await RunNativeCoreAsync(() => { action(); return true; }, token).ConfigureAwait(false);

    private async Task<T> RunNativeCoreAsync<T>(Func<T> action, CancellationToken token) =>
        await Task.Run(async () =>
        {
            await _nativeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                return action();
            }
            finally { _nativeGate.Release(); }
        }, CancellationToken.None).ConfigureAwait(false);

    private void RunFireAndForget(Action action)
    {
        lock (_commandSync) _commandTail = RunCommandAsync(_commandTail, action);
    }

    private async Task RunCommandAsync(Task previous, Action action)
    {
        await previous.ConfigureAwait(false);
        if (_disposed || IsStopping) return;
        try { await RunNativeCoreAsync(action, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Diagnostics.Log($"LibVLC command failed: {ex.GetType().Name} {ex.Message}"); }
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        BufferingPercent = e.Cache;
        BufferingChanged?.Invoke(this, e.Cache);
    }

    private void OnPlaybackChanged(object? sender, EventArgs e) => PlaybackChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _nativeGate.Wait(); StopCore(); }
        catch (Exception ex) { Diagnostics.Log($"LibVLC dispose failed: {ex.GetType().Name}"); }
        finally { _nativeGate.Release(); }
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();
        _mediaPlayer = null;
        _libVLC = null;
        ReleaseVideoOutput();
    }
}
