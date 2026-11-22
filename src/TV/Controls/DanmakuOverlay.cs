using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TV.Services.Playback;
using Windows.Foundation;
using Windows.UI;

namespace TV.Controls;

/// <summary>
/// High-performance VSync-driven danmaku overlay using TranslateTransform (zero layout thrash),
/// smooth playback-clock interpolation, element pooling, and collision-free lane scheduling.
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
        public double StartSec;
        public double DurationSec;
        public double WidthPx;
        public double SpeedPx;
    }

    private static readonly SolidColorBrush ShadowBrush = new(Color.FromArgb(204, 0, 0, 0));
    private static readonly Size InfiniteSize = new(double.PositiveInfinity, double.PositiveInfinity);

    private readonly List<DanmakuNode> _active = [];
    private readonly Stack<DanmakuNode> _pool = new();
    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];

    private DanmakuEntry[] _comments = [];
    private DanmakuPreferences _prefs = new();
    private int _nextIndex;

    // Smooth interpolated playback clock
    private double _anchorMediaSec;
    private long _anchorTimestamp;
    private double _playbackRate = 1.0;
    private bool _isPlaying;
    private bool _isBuffering;
    private double _lastRenderSec = -1;
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
        var previousOffset = _prefs.TimeOffsetSeconds;
        _prefs = preferences.Normalize();
        Opacity = _prefs.Opacity;

        // Live-update styling on currently visible items without clearing them
        foreach (var node in _active)
        {
            ApplyNodeStyle(node, node.RawRgb);
            if (node.Mode != DanmakuMode.Scroll)
            {
                node.Root.Measure(InfiniteSize);
                node.WidthPx = node.Root.DesiredSize.Width;
                node.Transform.X = Math.Max(8, (ActualWidth - node.WidthPx) * 0.5);
            }
        }

        if (Math.Abs(previousOffset - _prefs.TimeOffsetSeconds) > 0.01 && _lastRenderSec >= 0)
            ResetCursor(_lastRenderSec);

        UpdateRenderingHook();
    }

    public void SetComments(DanmakuEntry[] comments)
    {
        ClearActive();
        _comments = comments;
        _nextIndex = 0;
        _lastRenderSec = -1;
        if (_comments.Length > 0 && _anchorTimestamp != 0)
            ResetCursor(_anchorMediaSec);
        UpdateRenderingHook();
    }

    public void SyncPlayback(double mediaTimeSec, long capturedTimestamp, double playbackRate, bool isPlaying, bool isBuffering)
    {
        if (!double.IsFinite(mediaTimeSec) || mediaTimeSec < 0) return;
        var rate = playbackRate > 0.05 && double.IsFinite(playbackRate) ? playbackRate : 1.0;

        if (_anchorTimestamp == 0)
        {
            _anchorMediaSec = mediaTimeSec;
            _anchorTimestamp = capturedTimestamp;
            _playbackRate = rate;
            _isPlaying = isPlaying;
            _isBuffering = isBuffering;
            _lastRenderSec = mediaTimeSec;
            ResetCursor(mediaTimeSec);
            UpdateRenderingHook();
            return;
        }

        var currentEstimated = EstimateCurrentSec();
        var delta = mediaTimeSec - currentEstimated;

        // Detect seeks or large discontinuities (> 1.4s forward or > 0.25s backward)
        if (delta > 1.4 || delta < -0.25)
        {
            _anchorMediaSec = mediaTimeSec;
            _anchorTimestamp = capturedTimestamp;
            _playbackRate = rate;
            _isPlaying = isPlaying;
            _isBuffering = isBuffering;
            _lastRenderSec = mediaTimeSec;
            ClearActive();
            ResetCursor(mediaTimeSec);
            UpdateRenderingHook();
            return;
        }

        if (isPlaying && !isBuffering)
        {
            // Gently blend toward authoritative media timestamp to avoid backward hops or micro-stutter
            var blended = _lastRenderSec >= 0 ? Math.Max(_lastRenderSec, mediaTimeSec) : mediaTimeSec;
            _anchorMediaSec = blended;
            _anchorTimestamp = capturedTimestamp;
        }
        else if (_isPlaying && !isPlaying)
        {
            // Freeze cleanly at last rendered frame position on pause
            _anchorMediaSec = _lastRenderSec >= 0 ? _lastRenderSec : mediaTimeSec;
            _anchorTimestamp = Stopwatch.GetTimestamp();
        }

        _playbackRate = rate;
        _isPlaying = isPlaying;
        _isBuffering = isBuffering;
        UpdateRenderingHook();
    }

    public void Clear()
    {
        UnhookRendering();
        ClearActive();
        _comments = [];
        _nextIndex = 0;
        _anchorMediaSec = 0;
        _anchorTimestamp = 0;
        _lastRenderSec = -1;
        _isPlaying = false;
        _isBuffering = false;
    }

    public void ReleasePool()
    {
        Clear();
        _pool.Clear();
        _brushes.Clear();
    }

    private void UpdateRenderingHook()
    {
        var shouldRun = _prefs.Enabled && _comments.Length > 0 && _isPlaying && !_isBuffering;
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
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        foreach (var node in _active)
        {
            if (node.Mode != DanmakuMode.Scroll)
                node.Transform.X = Math.Max(8, (e.NewSize.Width - node.WidthPx) * 0.5);
        }
    }

    private double EstimateCurrentSec()
    {
        if (_anchorTimestamp == 0) return _anchorMediaSec;
        if (!_isPlaying || _isBuffering) return _anchorMediaSec;
        var elapsed = Stopwatch.GetElapsedTime(_anchorTimestamp).TotalSeconds;
        // Cap extrapolation if VLC snapshot stalls so danmaku doesn't run far ahead of frozen video
        var cappedElapsed = Math.Clamp(elapsed, 0, 0.75);
        return _anchorMediaSec + cappedElapsed * _playbackRate;
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_prefs.Enabled || !_isPlaying || _isBuffering || _comments.Length == 0) return;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var now = EstimateCurrentSec();
        if (_lastRenderSec >= 0 && now < _lastRenderSec) now = _lastRenderSec;
        _lastRenderSec = now;

        var effectiveNow = now + _prefs.TimeOffsetSeconds;

        // 1. Update positions of all active items & recycle finished ones
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var node = _active[i];
            var elapsed = effectiveNow - node.StartSec;
            if (elapsed >= node.DurationSec || elapsed < -0.5)
            {
                RecycleNode(i);
                continue;
            }

            if (node.Mode == DanmakuMode.Scroll)
            {
                node.Transform.X = width - elapsed * node.SpeedPx;
            }
        }

        // 2. Spawn newly due comments up to density cap
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
            if (!IsAllowed(entry)) continue;

            var node = RentNode(entry);
            node.Root.Measure(InfiniteSize);
            var itemWidth = Math.Max(20, node.Root.DesiredSize.Width);
            node.WidthPx = itemWidth;
            node.Mode = entry.Mode;
            node.StartSec = effectiveNow;

            if (entry.Mode == DanmakuMode.Scroll)
            {
                // Slightly longer comments move a tiny bit faster to feel natural, matching Bilibili conventions
                var lengthBoost = Math.Clamp(1.0 + itemWidth / (width * 2.5), 1.0, 1.28);
                var duration = baseScrollDuration / lengthBoost;
                var speed = (width + itemWidth) / duration;
                var lane = FindScrollLane(laneCount, width, speed, gapPx, effectiveNow);
                if (lane < 0)
                {
                    ReturnToPool(node);
                    continue;
                }

                node.Lane = lane;
                node.DurationSec = duration;
                node.SpeedPx = speed;
                node.Transform.X = width;
                node.Transform.Y = topMargin + lane * laneHeight;
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
                node.Transform.X = Math.Max(8, (width - itemWidth) * 0.5);
                node.Transform.Y = entry.Mode == DanmakuMode.Top
                    ? topMargin + lane * laneHeight
                    : Math.Max(topMargin, height - bottomMargin - (lane + 1) * laneHeight);
            }

            Children.Add(node.Root);
            _active.Add(node);
            spawnedThisFrame++;
        }
    }

    private int FindScrollLane(int laneCount, double canvasWidth, double newSpeed, double gapPx, double effectiveNow)
    {
        for (var lane = 0; lane < laneCount; lane++)
        {
            DanmakuNode? latest = null;
            for (var i = 0; i < _active.Count; i++)
            {
                var candidate = _active[i];
                if (candidate.Mode == DanmakuMode.Scroll && candidate.Lane == lane)
                {
                    if (latest is null || candidate.StartSec > latest.StartSec) latest = candidate;
                }
            }

            if (latest is null) return lane;

            var prevElapsed = effectiveNow - latest.StartSec;
            var prevLeft = canvasWidth - prevElapsed * latest.SpeedPx;
            var prevRight = prevLeft + latest.WidthPx;

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

    private bool IsAllowed(DanmakuEntry entry)
    {
        if (entry.Mode == DanmakuMode.Scroll && !_prefs.ShowScroll) return false;
        if (entry.Mode == DanmakuMode.Top && !_prefs.ShowTop) return false;
        if (entry.Mode == DanmakuMode.Bottom && !_prefs.ShowBottom) return false;
        if (!_prefs.ShowColor && entry.Rgb != 0xFFFFFFu) return false;
        return true;
    }

    private DanmakuNode RentNode(DanmakuEntry entry)
    {
        var node = _pool.Count > 0 ? _pool.Pop() : new DanmakuNode();
        node.RawRgb = entry.Rgb;
        node.Shadow.Text = entry.Text;
        node.Label.Text = entry.Text;
        ApplyNodeStyle(node, entry.Rgb);
        return node;
    }

    private void ApplyNodeStyle(DanmakuNode node, uint rawRgb)
    {
        var weight = _prefs.Bold ? FontWeights.SemiBold : FontWeights.Normal;
        node.Label.FontSize = _prefs.Size;
        node.Label.FontWeight = weight;
        node.Shadow.FontSize = _prefs.Size;
        node.Shadow.FontWeight = weight;
        node.Shadow.Visibility = _prefs.TextOutline ? Visibility.Visible : Visibility.Collapsed;

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
