using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using System.Runtime.InteropServices;
using TV.Models;

namespace TV.Services.Playback;

public sealed record PlaybackTrack(int Id, string? Name);
internal sealed record PlaybackTrackSelection(PlaybackTrack? Audio, PlaybackTrack? Subtitle, bool Paused = false, float Rate = 1);
public sealed record PlayerSnapshot(VLCState State, bool IsPlaying, long TimeMs, long LengthMs, bool CanSeek,
    float BufferingPercent, IReadOnlyList<PlaybackTrack> AudioTracks, IReadOnlyList<PlaybackTrack> SubtitleTracks,
    int AudioTrack, int SubtitleTrack, VideoFormat? VideoFormat, float Rate);

public sealed class PlayerService : IDisposable
{
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private readonly object _stopSync = new();
    private readonly object _commandSync = new();
    private Task _commandTail = Task.CompletedTask;
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
    private bool _videoOutputReady;
    private int _snapshotReads;
    private PlayerSnapshot _snapshot = new(VLCState.NothingSpecial, false, 0, 0, false, 100, [], [], -1, -1, null, 1);

    public PlayerService()
    {
        Core.Initialize();
        (LibVLC, MediaPlayer) = CreateCore(null);
    }

    private (LibVLC libVLC, MediaPlayer player) CreateCore(string[]? swapChainOptions)
    {
        var options = new List<string>
        {
            "--network-caching=3600", "--live-caching=1800", "--file-caching=900", "--disc-caching=500",
            "--no-osd", "--no-video-title-show", "--no-stats"
        };
        if (swapChainOptions is not null) options.AddRange(swapChainOptions);
        var libVLC = new LibVLC(options.ToArray());
        if (HasHdrSurface) libVLC.Log += OnNativeVideoLog;
        var player = new MediaPlayer(libVLC) { EnableHardwareDecoding = true, Volume = 85 };
        return (libVLC, player);
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
                MediaPlayer.Dispose();
                LibVLC.Dispose();
                ReleaseVideoOutput();
                HasHdrSurface = hdrDisplay && VideoDisplay.TryEnableHdrOutput(swapChain);
                (LibVLC, MediaPlayer) = CreateCore(swapChainOptions);
                _swapChainOptions = swapChainOptions;
                _swapChain = swapChain;
                _deviceContext = deviceContext;
                swapChain = deviceContext = 0;
                MediaPlayer.EnableHardwareDecoding = HardwareDecoding;
            }, CancellationToken.None).ConfigureAwait(false);
            _videoOutputReady = true;
            Diagnostics.Log($"LibVLC re-initialized with VideoView swapchain, HDR surface={HasHdrSurface}");
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

    public LibVLC LibVLC { get; private set; }
    public MediaPlayer MediaPlayer { get; private set; }
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
        _snapshot = new(VLCState.Opening, false, request.StartPositionMs, 0, false, 0, [], [], -1, -1, null, tracks?.Rate ?? 1);
        var media = new Media(LibVLC, request.Url, FromType.FromLocation);
        _currentMedia = media;
        try
        {
            MediaPlayer.EnableHardwareDecoding = HardwareDecoding;
            media.AddOption(HardwareDecoding ? ":avcodec-hw=d3d11va" : ":avcodec-hw=none");
            media.AddOption(":avcodec-threads=0");
            media.AddOption(request.Kind == PlaybackKind.Live ? $":live-caching={LiveCachingMs}" : $":network-caching={NetworkCachingMs}");
            media.AddOption(":file-caching=900");
            media.AddOption(":http-reconnect=true");
            media.AddOption(":http-forward-cookies=true");
            media.AddOption(":adaptive-use-access=true");
            if (request.UseAvformatDemuxer) media.AddOption(":demux=avformat");
            foreach (var subtitle in request.Subtitles) media.AddSlave(MediaSlaveType.Subtitle, 3, subtitle);
            foreach (var audio in request.AudioTracks) media.AddSlave(MediaSlaveType.Audio, 3, audio);
            foreach (var (key, value) in request.Headers)
            {
                if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) media.AddOption($":http-user-agent={value}");
                else if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || key.Equals("Referrer", StringComparison.OrdinalIgnoreCase)) media.AddOption($":http-referrer={value}");
            }
            MediaPlayer.Buffering += OnBuffering;
            MediaPlayer.EndReached += OnPlaybackChanged;
            MediaPlayer.EncounteredError += OnPlaybackChanged;
            if (!MediaPlayer.Play(media)) throw new InvalidOperationException("播放器无法启动该媒体，请尝试其他线路。");
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            StopCore();
            throw;
        }
    }

    public void TogglePause() => RunFireAndForget(() =>
    {
        if (_currentMedia is null) return;
        if (MediaPlayer.IsPlaying) MediaPlayer.Pause();
        else MediaPlayer.Play();
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    });

    internal int CurrentVideoHeight => _videoFormat?.Height ?? 0;

    public Task<PlayerSnapshot> ReadSnapshotAsync(CancellationToken token) => RunNativeAsync(() =>
    {
        ApplyPendingPositionCore();
        ApplyPendingTracksCore();
        var state = MediaPlayer.State;
        var video = ReadVideoFormatCore(state);
        var readTracks = ++_snapshotReads <= 4 || _snapshotReads % 6 == 0;
        var audio = readTracks ? DescribeTracks(MediaPlayer.AudioTrackDescription) : _snapshot.AudioTracks;
        var subtitles = readTracks ? DescribeTracks(MediaPlayer.SpuDescription) : _snapshot.SubtitleTracks;
        _snapshot = new PlayerSnapshot(state, MediaPlayer.IsPlaying, Math.Max(0, MediaPlayer.Time), Math.Max(0, MediaPlayer.Length),
            MediaPlayer.IsSeekable, BufferingPercent, audio, subtitles, MediaPlayer.AudioTrack, MediaPlayer.Spu, video, MediaPlayer.Rate);
        return _snapshot;
    }, token);

    private VideoFormat? ReadVideoFormatCore(VLCState state)
    {
        if (_videoFormat is not null || _currentMedia is null || state is not (VLCState.Playing or VLCState.Paused)) return _videoFormat;
        uint width = 0, height = 0;
        var tracks = _currentMedia.Tracks.Where(track => track.TrackType == TrackType.Video && track.Data.Video.Height > 0).ToArray();
        if (tracks.Length == 0)
        {
            if (MediaPlayer.Size(0, ref width, ref height) && height > 0) _videoFormat = new VideoFormat((int)height, 0);
            return _videoFormat;
        }
        var track = tracks.FirstOrDefault(track => track.Id == MediaPlayer.VideoTrack);
        var video = track.Data.Video.Height > 0 ? track.Data.Video : tracks[0].Data.Video;
        var frameRate = video.FrameRateDen == 0 ? 0 : VideoFormat.HighFrameRate((double)video.FrameRateNum / video.FrameRateDen);
        _videoFormat = new VideoFormat((int)video.Height, frameRate);
        return _videoFormat;
    }

    public void Seek(long positionMs) => RunFireAndForget(() =>
    {
        if (MediaPlayer.Length > 0 && MediaPlayer.IsSeekable)
        {
            _pendingSeekPosition = null;
            MediaPlayer.Time = Math.Clamp(positionMs, 0, MediaPlayer.Length);
            _positionApplied = true;
        }
        else if (CurrentRequest?.Kind == PlaybackKind.Vod) _pendingSeekPosition = positionMs;
    });

    private void StopCore()
    {
        if (_currentMedia is null) return;
        MediaPlayer.Buffering -= OnBuffering;
        MediaPlayer.EndReached -= OnPlaybackChanged;
        MediaPlayer.EncounteredError -= OnPlaybackChanged;
        var current = _currentMedia;
        try { MediaPlayer.Stop(); }
        finally
        {
            current.Dispose();
            _currentMedia = null;
            _pendingSeekPosition = null;
            _pendingTracks = null;
            BufferingPercent = 100;
            _snapshot = new(VLCState.Stopped, false, _snapshot.TimeMs, _snapshot.LengthMs, false, 100,
                _snapshot.AudioTracks, _snapshot.SubtitleTracks, -1, -1, _snapshot.VideoFormat, _snapshot.Rate);
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
        if (_positionApplied && !_pendingSeekPosition.HasValue) return;
        if (MediaPlayer.Length <= 0 || !MediaPlayer.IsSeekable) return;
        if (_pendingSeekPosition is { } seek)
        {
            MediaPlayer.Time = Math.Clamp(seek, 0, MediaPlayer.Length);
            _pendingSeekPosition = null;
            _positionApplied = true;
        }
        else if (!_positionApplied && _pendingStartPosition > 0)
        {
            MediaPlayer.Time = Math.Min(_pendingStartPosition, Math.Max(0, MediaPlayer.Length - 3000));
            _positionApplied = true;
        }
    }

    internal Task<PlaybackTrackSelection> CaptureTrackSelectionAsync(CancellationToken token) => RunNativeAsync(() =>
    {
        var audio = DescribeTrack(MediaPlayer.AudioTrackDescription, MediaPlayer.AudioTrack);
        var subtitle = DescribeTrack(MediaPlayer.SpuDescription, MediaPlayer.Spu);
        return new PlaybackTrackSelection(audio, subtitle, MediaPlayer.State == VLCState.Paused, MediaPlayer.Rate);
    }, token);

    private void ApplyPendingTracksCore()
    {
        if (_pendingTracks is not { } tracks || MediaPlayer.State is not (VLCState.Playing or VLCState.Paused)) return;
        if (_trackAttempts == 0)
        {
            if (tracks.Rate > 0 && tracks.Rate != 1) MediaPlayer.SetRate(tracks.Rate);
            if (tracks.Paused) MediaPlayer.SetPause(true);
        }
        var audio = tracks.Audio is { } a && !RestoreTrack(a, MediaPlayer.AudioTrackDescription, SelectAudioTrackCore) ? a : null;
        var subtitle = tracks.Subtitle is { } s && !RestoreTrack(s, MediaPlayer.SpuDescription, MediaPlayer.SetSpu) ? s : null;
        _pendingTracks = ++_trackAttempts >= 8 || audio is null && subtitle is null ? null : tracks with { Audio = audio, Subtitle = subtitle };
    }

    internal Task<bool> SelectTrackAsync(bool subtitle, int id) => RunNativeAsync(() =>
    {
        if (_disposed) return false;
        if (_pendingTracks is { } tracks) _pendingTracks = subtitle ? tracks with { Subtitle = null } : tracks with { Audio = null };
        return subtitle ? MediaPlayer.SetSpu(id) : SelectAudioTrackCore(id);
    }, CancellationToken.None);

    private bool SelectAudioTrackCore(int id)
    {
        if (MediaPlayer.AudioTrack == id) return true;
        var position = MediaPlayer.Time;
        if (!MediaPlayer.SetAudioTrack(id)) return false;
        if (id >= 0 && position >= 0 && CurrentRequest?.SeekOnAudioTrackChange == true && MediaPlayer.IsSeekable) MediaPlayer.Time = position;
        return true;
    }

    internal Task<bool> AddSubtitleAsync(Uri file) => RunNativeAsync(() => MediaPlayer.AddSlave(MediaSlaveType.Subtitle, file.AbsoluteUri, true), CancellationToken.None);
    internal void SetRate(float rate) => RunFireAndForget(() => MediaPlayer.SetRate(rate));
    internal void SetVolume(int volume) => RunFireAndForget(() => MediaPlayer.Volume = volume);
    internal void SetBufferingPause(bool paused) => RunFireAndForget(() => MediaPlayer.SetPause(paused));

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
        MediaPlayer.Dispose();
        LibVLC.Dispose();
        ReleaseVideoOutput();
    }
}
