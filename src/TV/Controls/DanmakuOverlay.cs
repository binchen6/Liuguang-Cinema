using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TV.Services.Playback;
using Windows.Foundation;
using Windows.UI;

namespace TV.Controls;

/// <summary>
/// Strictly monotonic PLL-slewed playback clock for danmaku rendering.
/// Absorbs coarse/jittery LibVLC MediaPlayer.Time updates without ever rewinding or wiping active comments.
/// </summary>
internal sealed class DanmakuClock
{
    private bool _initialized;
    private double _clockSec;
    private double _lastMediaSec;
    private long _lastMediaTs;
    private long _lastFrameTs;
    private double _playbackRate = 1.0;
    private bool _isPlaying;
    private bool _isBuffering;

    public bool IsInitialized => _initialized;
    public double ClockSec => _clockSec;
    public bool IsPlaying => _isPlaying;
    public bool IsBuffering => _isBuffering;
    public double PlaybackRate => _playbackRate;

    public void Reset()
    {
        _initialized = false;
        _clockSec = 0;
        _lastMediaSec = 0;
        _lastMediaTs = 0;
        _lastFrameTs = 0;
        _playbackRate = 1.0;
        _isPlaying = false;
        _isBuffering = false;
    }

    public void SeekTo(double targetSec, long nowTs)
    {
        if (!double.IsFinite(targetSec) || targetSec < 0) return;
        _initialized = true;
        _clockSec = targetSec;
        _lastMediaSec = targetSec;
        _lastMediaTs = nowTs;
        _lastFrameTs = nowTs;
    }

    public void SetPlayingImmediate(bool isPlaying, long nowTs)
    {
        var resuming = !_isPlaying && isPlaying;
        _isPlaying = isPlaying;
        if (resuming)
        {
            _lastMediaSec = _clockSec;
            _lastMediaTs = nowTs;
            _lastFrameTs = nowTs;
        }
    }

    /// <summary>
    /// Syncs authoritative media snapshot. Returns true ONLY when a genuine discontinuity/seek occurred
    /// that requires clearing active comments and repositioning the cursor.
    /// </summary>
    public bool Sync(double mediaTimeSec, long capturedTs, double playbackRate, bool isPlaying, bool isBuffering, long nowTs)
    {
        if (!double.IsFinite(mediaTimeSec) || mediaTimeSec < 0) return false;
        var rate = playbackRate > 0.05 && double.IsFinite(playbackRate) ? playbackRate : 1.0;
        var wasRunning = _isPlaying && !_isBuffering;
        var willRun = isPlaying && !isBuffering;

        _playbackRate = rate;
        _isPlaying = isPlaying;
        _isBuffering = isBuffering;

        if (!_initialized)
        {
            _initialized = true;
            _clockSec = mediaTimeSec;
            _lastMediaSec = mediaTimeSec;
            _lastMediaTs = capturedTs != 0 ? capturedTs : nowTs;
            _lastFrameTs = nowTs;
            return true;
        }

        // Compare authoritative mediaTimeSec against previous authoritative _lastMediaSec (NOT extrapolated _clockSec!)
        // so coarse LibVLC PTS updates or brief frame jitter NEVER trigger false backward seeks.
        var rawJump = mediaTimeSec - _lastMediaSec;
        if (rawJump < -1.5 || rawJump > 3.5)
        {
            _clockSec = mediaTimeSec;
            _lastMediaSec = mediaTimeSec;
            _lastMediaTs = capturedTs != 0 ? capturedTs : nowTs;
            _lastFrameTs = nowTs;
            return true;
        }

        // Only refresh anchor timestamp when VLC actually advanced its reported media time,
        // or when transitioning out of pause/buffering. Keeping the anchor when VLC repeats the same
        // coarse timestamp prevents sawtooth pull-back.
        var mediaAdvanced = Math.Abs(mediaTimeSec - _lastMediaSec) > 0.004;
        if (mediaAdvanced)
        {
            _lastMediaSec = mediaTimeSec;
            _lastMediaTs = capturedTs != 0 ? capturedTs : nowTs;
        }
        else if (!wasRunning && willRun)
        {
            // Resumed from pause/buffering: anchor smoothly from current rendered clock if close
            if (Math.Abs(mediaTimeSec - _clockSec) > 1.0)
                _lastMediaSec = mediaTimeSec;
            else
                _lastMediaSec = Math.Max(_clockSec, mediaTimeSec);
            _lastMediaTs = nowTs;
        }

        if (!wasRunning && willRun)
            _lastFrameTs = nowTs;

        return false;
    }

    /// <summary>
    /// Advances the display clock by one frame using PLL slewing.
    /// Guaranteed to return a non-negative stepSec >= 0 so scrolling danmaku can NEVER move backward.
    /// </summary>
    public double StepFrame(long nowTs, out bool jumpedForward)
    {
        jumpedForward = false;
        if (!_initialized || !_isPlaying || _isBuffering) return 0;

        var realDt = _lastFrameTs == 0
            ? 0.016
            : Math.Clamp(Stopwatch.GetElapsedTime(_lastFrameTs, nowTs).TotalSeconds, 0.0, 0.05);
        _lastFrameTs = nowTs;
        if (realDt <= 0) return 0;

        var sinceAnchor = _lastMediaTs == 0
            ? 0.0
            : Math.Clamp(Stopwatch.GetElapsedTime(_lastMediaTs, nowTs).TotalSeconds, 0.0, 0.85);
        var targetSec = _lastMediaSec + sinceAnchor * _playbackRate;
        var err = targetSec - _clockSec;

        if (err > 2.5)
        {
            // Large forward recovery after UI freeze: fast-forward clock cleanly without moving backward
            _clockSec = targetSec;
            jumpedForward = true;
            return realDt * _playbackRate;
        }

        double slew;
        if (err > 0.05)
            slew = Math.Clamp(1.0 + err * 0.50, 1.0, 1.35);
        else if (err < -1.5)
            slew = 0.0; // Hold gently in place if video stalled without reporting buffering; never rewind!
        else if (err < -0.05)
            slew = Math.Clamp(1.0 + err * 0.45, 0.45, 1.0);
        else
            slew = 1.0;

        var stepSec = realDt * _playbackRate * slew;
        _clockSec += stepSec;
        return stepSec;
    }
}

/// <summary>
/// High-performance VSync-driven danmaku overlay using TranslateTransform (zero layout thrash),
/// strictly monotonic PLL-slewed clock (zero mid-scroll rewind), element pooling, and collision-free lane scheduling.
/// </summary>
public sealed class DanmakuOverlay : Canvas
{
    private sealed class DanmakuNode
    {
        public DanmakuNode()
        {
            Transform = new TranslateTransform();
            Shadow = new TextBlock
            {
                IsHitTestVisible = false,
                Foreground = ShadowBrush,
                RenderTransform = new TranslateTransform { X = 1.2, Y = 1.2 }
            };
            Label = new TextBlock { IsHitTestVisible = false };
            Root = new Grid
            {
                IsHitTestVisible = false,
                RenderTransform = Transform,
                Children = { Shadow, Label }
            };
            SetLeft(Root, 0);
            SetTop(Root, 0);
        }

        public Grid Root { get; }
        public TextBlock Shadow { get; }
        public TextBlock Label { get; }
        public TranslateTransform Transform { get; }

        public DanmakuMode Mode;
        public uint RawRgb;
        public int Lane;
        public double ElapsedSec;
        public double DurationSec;
        public double WidthPx;
        public double SpeedPx;
        public double CurrentX;
        public int AppliedFontSize;
        public bool AppliedBold;
        public bool AppliedOutline = true;
    }

    private static readonly SolidColorBrush ShadowBrush = new(Color.FromArgb(204, 0, 0, 0));
    private static readonly Size InfiniteSize = new(double.PositiveInfinity, double.PositiveInfinity);

    private readonly List<DanmakuNode> _active = [];
    private readonly DanmakuNode?[] _lastInScrollLane = new DanmakuNode?[24];
    private readonly Stack<DanmakuNode> _pool = new();
    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];
    private readonly RectangleGeometry _clipGeometry = new();
    private readonly DanmakuClock _clock = new();

    private DanmakuEntry[] _comments = [];
    private DanmakuPreferences _prefs = new();
    private int _nextIndex;
    private double _lastWidth;
    private bool _renderingHooked;

    public DanmakuOverlay()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        SizeChanged += OnOverlaySizeChanged;
    }

    public int LoadedCount => _comments.Length;
    public int ActiveCount => _active.Count;

    public void SetPreferences(DanmakuPreferences preferences)
    {
        var previous = _prefs;
        _prefs = preferences.Normalize();
        Opacity = _prefs.Opacity;
        if (!_prefs.Enabled)
        {
            ClearActive();
            UpdateRenderingHook();
            return;
        }

        var changedMetrics = previous.Size != _prefs.Size || previous.Bold != _prefs.Bold ||
            previous.TextOutline != _prefs.TextOutline;
        var changedStyle = changedMetrics || previous.Color != _prefs.Color;
        var changedTypes = previous.ShowScroll != _prefs.ShowScroll || previous.ShowTop != _prefs.ShowTop ||
            previous.ShowBottom != _prefs.ShowBottom || previous.ShowColor != _prefs.ShowColor;

        if (changedStyle || changedTypes)
        {
            for (var i = _active.Count - 1; i >= 0; i--)
            {
                var node = _active[i];
                if (changedTypes && !IsAllowed(node.Mode, node.RawRgb))
                {
                    RecycleNode(i);
                    continue;
                }
                if (changedStyle) ApplyNodeStyle(node, node.RawRgb);
                if (!changedMetrics) continue;
                node.Root.Measure(InfiniteSize);
                node.WidthPx = Math.Max(20, node.Root.DesiredSize.Width);
                if (node.Mode != DanmakuMode.Scroll)
                    node.Transform.X = Math.Max(8, (ActualWidth - node.WidthPx) * 0.5);
            }
        }

        if (_clock.IsInitialized && (!previous.Enabled || previous.TimeOffsetSeconds != _prefs.TimeOffsetSeconds))
            ResetCursor(_clock.ClockSec);

        UpdateRenderingHook();
    }

    public void SetComments(DanmakuEntry[] comments)
    {
        if (ReferenceEquals(_comments, comments)) return;
        ClearActive();
        _comments = comments;
        _nextIndex = 0;
        if (_comments.Length > 0 && _clock.IsInitialized)
            ResetCursor(_clock.ClockSec);
        UpdateRenderingHook();
    }

    /// <summary>
    /// Immediately freezes or resumes danmaku motion on user Play/Pause click (0ms visual sync).
    /// </summary>
    public void SetPlayingImmediate(bool isPlaying)
    {
        _clock.SetPlayingImmediate(isPlaying, Stopwatch.GetTimestamp());
        UpdateRenderingHook();
    }

    /// <summary>
    /// Explicit seek notification: clears active comments and repositions cleanly to the seek target.
    /// </summary>
    public void NotifySeek(double targetSec)
    {
        if (!double.IsFinite(targetSec) || targetSec < 0) return;
        _clock.SeekTo(targetSec, Stopwatch.GetTimestamp());
        ClearActive();
        ResetCursor(targetSec);
        UpdateRenderingHook();
    }

    public void SyncPlayback(double mediaTimeSec, long capturedTimestamp, double playbackRate, bool isPlaying, bool isBuffering)
    {
        var nowTs = Stopwatch.GetTimestamp();
        var discontinuity = _clock.Sync(mediaTimeSec, capturedTimestamp, playbackRate, isPlaying, isBuffering, nowTs);
        if (discontinuity)
        {
            ClearActive();
            ResetCursor(mediaTimeSec);
        }
        else if (!_renderingHooked && _active.Count == 0)
        {
            // No frame callbacks run for a disabled or exhausted overlay. Keep its clock current for the next enable or reload.
            _clock.SeekTo(mediaTimeSec, nowTs);
        }
        UpdateRenderingHook();
    }

    public void Clear()
    {
        UnhookRendering();
        ClearActive();
        _comments = [];
        _nextIndex = 0;
        _clock.Reset();
    }

    public void ReleasePool()
    {
        Clear();
        _pool.Clear();
        _brushes.Clear();
    }

    private void UpdateRenderingHook()
    {
        var hasWork = _nextIndex < _comments.Length || _active.Count > 0;
        var shouldRun = _prefs.Enabled && hasWork && _clock.IsPlaying && !_clock.IsBuffering;
        if (shouldRun && !_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }
        else if (!shouldRun && _renderingHooked)
        {
            UnhookRendering();
        }
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    private void OnOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var newWidth = e.NewSize.Width;
        var newHeight = e.NewSize.Height;
        if (newWidth <= 0 || newHeight <= 0) return;
        _clipGeometry.Rect = new Rect(0, 0, newWidth, newHeight);
        if (!ReferenceEquals(Clip, _clipGeometry)) Clip = _clipGeometry;

        var scaleX = _lastWidth > 0 ? newWidth / _lastWidth : 1.0;
        _lastWidth = newWidth;

        foreach (var node in _active)
        {
            if (node.Mode != DanmakuMode.Scroll)
            {
                node.Transform.X = Math.Max(8, (newWidth - node.WidthPx) * 0.5);
            }
            else if (Math.Abs(scaleX - 1.0) > 0.005)
            {
                node.CurrentX *= scaleX;
                node.SpeedPx *= scaleX;
                node.Transform.X = node.CurrentX;
            }
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_prefs.Enabled || !_clock.IsPlaying || _clock.IsBuffering || _comments.Length == 0) return;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        if (_lastWidth <= 0) _lastWidth = width;

        var stepSec = _clock.StepFrame(Stopwatch.GetTimestamp(), out var jumpedForward);
        if (stepSec <= 0 && !jumpedForward) return;
        if (jumpedForward) ResetCursor(_clock.ClockSec);

        // 1. Monotonically advance all active items & recycle finished ones
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var node = _active[i];
            node.ElapsedSec += stepSec;

            if (node.Mode == DanmakuMode.Scroll)
            {
                node.CurrentX -= stepSec * node.SpeedPx;
                if (node.CurrentX + node.WidthPx < -12 || node.ElapsedSec >= node.DurationSec + 1.0)
                {
                    RecycleNode(i);
                    continue;
                }
                node.Transform.X = node.CurrentX;
            }
            else if (node.ElapsedSec >= node.DurationSec)
            {
                RecycleNode(i);
            }
        }

        // 2. Spawn newly due comments up to density cap
        var effectiveNow = _clock.ClockSec + _prefs.TimeOffsetSeconds;
        var maxOnScreen = _prefs.Density switch { 0 => 25, 1 => 60, 2 => 110, _ => 180 };
        var spawnedThisFrame = 0;
        var laneHeight = _prefs.Size + 10.0;
        var topMargin = 52.0;
        var bottomMargin = 68.0;
        var usableHeight = Math.Max(laneHeight, (height - topMargin - bottomMargin) * _prefs.DisplayArea);
        var laneCount = Math.Clamp((int)(usableHeight / laneHeight), 1, 24);
        var gapPx = 22.0 + (3 - _prefs.Density) * 16.0;
        var widthFactor = Math.Clamp(width / 1280.0, 0.72, 1.35);
        var baseScrollDuration = (8.2 * widthFactor) / _prefs.Speed;
        var pinnedDuration = 4.0 / Math.Clamp(_prefs.Speed, 0.75, 1.5);

        while (_nextIndex < _comments.Length && _comments[_nextIndex].Seconds <= effectiveNow)
        {
            var entry = _comments[_nextIndex++];

            // Skip stale entries that are already too old (e.g. burst after lag)
            if (effectiveNow - entry.Seconds > 0.8) continue;
            if (_active.Count >= maxOnScreen || spawnedThisFrame >= 6) continue;
            if (!IsAllowed(entry.Mode, entry.Rgb)) continue;

            var node = RentNode(entry);
            node.Root.Measure(InfiniteSize);
            var itemWidth = Math.Max(20, node.Root.DesiredSize.Width);
            node.WidthPx = itemWidth;
            node.Mode = entry.Mode;
            node.ElapsedSec = 0;

            if (entry.Mode == DanmakuMode.Scroll)
            {
                // Slightly longer comments move a tiny bit faster to feel natural, matching Bilibili conventions
                var lengthBoost = Math.Clamp(1.0 + itemWidth / (width * 2.5), 1.0, 1.28);
                var duration = baseScrollDuration / lengthBoost;
                var speed = (width + itemWidth) / duration;
                var lane = FindScrollLane(laneCount, width, speed, gapPx);
                if (lane < 0)
                {
                    ReturnToPool(node);
                    continue;
                }

                node.Lane = lane;
                node.DurationSec = duration;
                node.SpeedPx = speed;
                node.CurrentX = width;
                node.Transform.X = width;
                node.Transform.Y = topMargin + lane * laneHeight;
                if (lane < _lastInScrollLane.Length) _lastInScrollLane[lane] = node;
            }
            else
            {
                var lane = FindPinnedLane(entry.Mode, laneCount);
                if (lane < 0)
                {
                    ReturnToPool(node);
                    continue;
                }

                node.Lane = lane;
                node.DurationSec = pinnedDuration;
                node.SpeedPx = 0;
                node.CurrentX = Math.Max(8, (width - itemWidth) * 0.5);
                node.Transform.X = node.CurrentX;
                node.Transform.Y = entry.Mode == DanmakuMode.Top
                    ? topMargin + lane * laneHeight
                    : Math.Max(topMargin, height - bottomMargin - (lane + 1) * laneHeight);
            }

            Children.Add(node.Root);
            _active.Add(node);
            spawnedThisFrame++;
        }

        if (_nextIndex >= _comments.Length && _active.Count == 0) UnhookRendering();
    }

    private int FindScrollLane(int laneCount, double canvasWidth, double newSpeed, double gapPx)
    {
        var count = Math.Min(laneCount, _lastInScrollLane.Length);
        for (var lane = 0; lane < count; lane++)
        {
            var latest = _lastInScrollLane[lane];
            if (latest is null) return lane;

            var prevRight = latest.CurrentX + latest.WidthPx;

            // Tail of previous comment must have cleared right edge with safety gap
            if (prevRight + gapPx > canvasWidth) continue;

            // Ensure faster new comment will not overtake previous before previous leaves screen
            if (newSpeed > latest.SpeedPx && prevRight > 0)
            {
                var timeForPrevExit = prevRight / latest.SpeedPx;
                var timeForNewHitLeft = canvasWidth / newSpeed;
                if (timeForNewHitLeft < timeForPrevExit) continue;
            }

            return lane;
        }

        return -1;
    }

    private int FindPinnedLane(DanmakuMode mode, int laneCount)
    {
        for (var lane = 0; lane < laneCount; lane++)
        {
            var occupied = false;
            for (var i = 0; i < _active.Count; i++)
            {
                var candidate = _active[i];
                if (candidate.Mode == mode && candidate.Lane == lane)
                {
                    occupied = true;
                    break;
                }
            }
            if (!occupied) return lane;
        }
        return -1;
    }

    private bool IsAllowed(DanmakuMode mode, uint rgb) => (_prefs.ShowColor || rgb == 0xFFFFFFu) && mode switch
    {
        DanmakuMode.Scroll => _prefs.ShowScroll,
        DanmakuMode.Top => _prefs.ShowTop,
        DanmakuMode.Bottom => _prefs.ShowBottom,
        _ => true
    };

    private DanmakuNode RentNode(DanmakuEntry entry)
    {
        var node = _pool.Count > 0 ? _pool.Pop() : new DanmakuNode();
        node.RawRgb = entry.Rgb;
        node.Label.Text = entry.Text;
        node.Shadow.Text = _prefs.TextOutline ? entry.Text : string.Empty;
        ApplyNodeStyle(node, entry.Rgb);
        return node;
    }

    private void ApplyNodeStyle(DanmakuNode node, uint rawRgb)
    {
        if (node.AppliedFontSize != _prefs.Size)
        {
            node.AppliedFontSize = _prefs.Size;
            node.Label.FontSize = _prefs.Size;
            node.Shadow.FontSize = _prefs.Size;
        }
        if (node.AppliedBold != _prefs.Bold)
        {
            node.AppliedBold = _prefs.Bold;
            var weight = _prefs.Bold ? FontWeights.SemiBold : FontWeights.Normal;
            node.Label.FontWeight = weight;
            node.Shadow.FontWeight = weight;
        }
        if (node.AppliedOutline != _prefs.TextOutline)
        {
            node.AppliedOutline = _prefs.TextOutline;
            node.Shadow.Visibility = _prefs.TextOutline ? Visibility.Visible : Visibility.Collapsed;
            node.Shadow.Text = _prefs.TextOutline ? node.Label.Text : string.Empty;
        }

        var rgb = _prefs.Color switch
        {
            1 => 0xFFFFFFu,
            2 => 0xFFD18Au,
            3 => 0xA7B8FFu,
            _ => rawRgb
        };
        node.Label.Foreground = GetBrush(rgb);
    }

    private SolidColorBrush GetBrush(uint rgb)
    {
        if (_brushes.TryGetValue(rgb, out var brush)) return brush;
        if (_brushes.Count > 64) _brushes.Clear();
        brush = new SolidColorBrush(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        _brushes[rgb] = brush;
        return brush;
    }

    private void RecycleNode(int activeIndex)
    {
        var node = _active[activeIndex];
        if (node.Mode == DanmakuMode.Scroll && node.Lane >= 0 && node.Lane < _lastInScrollLane.Length &&
            ReferenceEquals(_lastInScrollLane[node.Lane], node))
        {
            _lastInScrollLane[node.Lane] = null;
        }
        _active.RemoveAt(activeIndex);
        Children.Remove(node.Root);
        ReturnToPool(node);
    }

    private void ReturnToPool(DanmakuNode node)
    {
        if (_pool.Count < 160)
        {
            node.Label.Text = string.Empty;
            node.Shadow.Text = string.Empty;
            _pool.Push(node);
        }
    }

    private void ClearActive()
    {
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var node = _active[i];
            Children.Remove(node.Root);
            ReturnToPool(node);
        }
        _active.Clear();
        Array.Clear(_lastInScrollLane);
    }

    private void ResetCursor(double mediaSec)
    {
        var target = mediaSec + _prefs.TimeOffsetSeconds - 0.15;
        // Binary search for first entry >= target
        var lo = 0;
        var hi = _comments.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (_comments[mid].Seconds < target) lo = mid + 1;
            else hi = mid;
        }
        _nextIndex = lo;
    }
}
