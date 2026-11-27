using System.Diagnostics;
using LibVLCSharp.Shared;

namespace TV.Services.Playback;

/// <summary>
/// Pure, deterministic state machine governing seek target locking, slider snap-back prevention,
/// pause/play intent stability across transient seek buffering, and non-invasive stall detection.
/// Eliminates pause/play oscillation loops and backward-seek false stall triggers.
/// </summary>
internal sealed class PlaybackSeekCoordinator
{
    private const long SeekCommitWindowMs = 2600;
    private const long SeekMatchToleranceMs = 2500;
    private const long PostSeekStallGraceMs = 4500;
    private const long BufferingIndicatorDelayMs = 220;
    private const long PauseIntentOverrideMs = 900;

    private bool _isDragging;
    private bool _manualPaused;
    private bool _hasPlayedMedia;
    private bool _pauseOverrideActive;
    private bool _pauseDesiredPlaying;
    private long _pauseOverrideUntilMs;

    private bool _seekLockActive;
    private long _seekTargetMs;
    private long _seekGeneration;
    private long _seekLockUntilMs;
    private long _postSeekGraceUntilMs;
    private long _bufferingStartedMs = -1;

    private long _lastProgressMs = -1;
    private long _lastProgressAtMs;
    private int _stallEvents;
    private long _stallWindowStartMs;
    private bool _stallCountedForCurrentBuffering;

    public bool IsDragging => _isDragging;
    public bool ManualPaused => _manualPaused;
    public bool HasPlayedMedia => _hasPlayedMedia;
    public long ActiveSeekTargetMs => _seekLockActive ? _seekTargetMs : -1;

    public void Reset(long nowMs, long startPositionMs = 0)
    {
        _isDragging = false;
        _manualPaused = false;
        _hasPlayedMedia = false;
        _pauseOverrideActive = false;
        _pauseDesiredPlaying = false;
        _pauseOverrideUntilMs = 0;
        _seekLockActive = startPositionMs > 0;
        _seekTargetMs = Math.Max(0, startPositionMs);
        _seekGeneration = 0;
        _seekLockUntilMs = startPositionMs > 0 ? nowMs + SeekCommitWindowMs : 0;
        _postSeekGraceUntilMs = nowMs + PostSeekStallGraceMs;
        _bufferingStartedMs = -1;
        _lastProgressMs = Math.Max(0, startPositionMs);
        _lastProgressAtMs = nowMs;
        _stallEvents = 0;
        _stallWindowStartMs = 0;
        _stallCountedForCurrentBuffering = false;
    }

    public void BeginDrag() => _isDragging = true;

    public long CommitSeek(long targetMs, long durationMs, long seekGeneration, long nowMs)
    {
        _isDragging = false;
        var clamped = durationMs > 0 ? Math.Clamp(targetMs, 0, durationMs) : Math.Max(0, targetMs);
        _seekLockActive = true;
        _seekTargetMs = clamped;
        _seekGeneration = seekGeneration;
        _seekLockUntilMs = nowMs + SeekCommitWindowMs;
        _postSeekGraceUntilMs = nowMs + PostSeekStallGraceMs;
        // Reset progress baseline immediately so backward seeks NEVER look like a stall!
        _lastProgressMs = clamped;
        _lastProgressAtMs = nowMs;
        _stallCountedForCurrentBuffering = false;
        _bufferingStartedMs = -1;
        return clamped;
    }

    public long ComputeRelativeSeekTarget(double deltaSeconds, long snapshotTimeMs, long durationMs, long nowMs)
    {
        var baseTime = _seekLockActive && nowMs < _seekLockUntilMs ? _seekTargetMs : snapshotTimeMs;
        var target = baseTime + (long)Math.Round(deltaSeconds * 1000.0);
        return durationMs > 0 ? Math.Clamp(target, 0, durationMs) : Math.Max(0, target);
    }

    public bool TogglePause(bool currentDisplayedPlaying, long nowMs)
    {
        var desiredPlaying = !currentDisplayedPlaying;
        _manualPaused = !desiredPlaying;
        _pauseDesiredPlaying = desiredPlaying;
        _pauseOverrideActive = true;
        _pauseOverrideUntilMs = nowMs + PauseIntentOverrideMs;
        if (desiredPlaying)
        {
            _lastProgressAtMs = nowMs;
            _postSeekGraceUntilMs = Math.Max(_postSeekGraceUntilMs, nowMs + 2500);
            _stallCountedForCurrentBuffering = false;
        }
        return desiredPlaying;
    }

    public readonly record struct TickDecision(
        bool EffectivePlaying,
        bool ShowBuffering,
        long DisplayPositionMs,
        bool ShouldUpdatePosition,
        bool SuggestLowerQuality);

    public TickDecision EvaluateTick(PlayerSnapshot snapshot, long currentUiPositionMs, long nowMs, bool isLocalFile)
    {
        if (snapshot.State == VLCState.Playing)
            _hasPlayedMedia = true;

        // 1. Resolve logical playing state — NEVER flicker Paused during transient Buffering/Opening when user did not pause!
        var effectivePlaying = snapshot.IsPlaying;
        if (_pauseOverrideActive)
        {
            if (snapshot.IsPlaying == _pauseDesiredPlaying || nowMs >= _pauseOverrideUntilMs)
                _pauseOverrideActive = false;
            else
                effectivePlaying = _pauseDesiredPlaying;
        }
        else if (!_manualPaused && _hasPlayedMedia && snapshot.State is VLCState.Buffering or VLCState.Opening)
        {
            effectivePlaying = true;
        }
        else if (_manualPaused && snapshot.State == VLCState.Paused)
        {
            effectivePlaying = false;
        }

        // 2. Resolve display position without snap-back during drag or seek commit settle window
        long displayPos = currentUiPositionMs;
        var shouldUpdatePos = false;
        if (!_isDragging)
        {
            if (_seekLockActive)
            {
                var generationCaughtUp = snapshot.SeekGeneration >= _seekGeneration;
                var nearTarget = Math.Abs(snapshot.TimeMs - _seekTargetMs) <= SeekMatchToleranceMs;
                if ((generationCaughtUp && nearTarget && snapshot.State is VLCState.Playing or VLCState.Paused) || nowMs >= _seekLockUntilMs)
                {
                    _seekLockActive = false;
                    displayPos = snapshot.TimeMs;
                    shouldUpdatePos = true;
                }
                else
                {
                    displayPos = _seekTargetMs;
                    shouldUpdatePos = false;
                }
            }
            else
            {
                displayPos = snapshot.TimeMs;
                shouldUpdatePos = true;
            }
        }

        // 3. Debounce buffering indicator slightly after seeks so fast cached seeks don't flash a spinner
        var rawBuffering = snapshot.State is VLCState.Opening or VLCState.Buffering;
        bool showBuffering;
        if (rawBuffering)
        {
            if (_bufferingStartedMs < 0) _bufferingStartedMs = nowMs;
            showBuffering = !_hasPlayedMedia || (nowMs - _bufferingStartedMs >= BufferingIndicatorDelayMs);
        }
        else
        {
            _bufferingStartedMs = -1;
            showBuffering = false;
        }

        // 4. Non-invasive stall detection (ONLY for adaptive quality downgrade; NEVER forces SetPause!)
        var suggestLowerQuality = false;
        if (_hasPlayedMedia && !_manualPaused && !_isDragging && !isLocalFile)
        {
            if (Math.Abs(snapshot.TimeMs - _lastProgressMs) > 40 && snapshot.State == VLCState.Playing)
            {
                _lastProgressMs = snapshot.TimeMs;
                _lastProgressAtMs = nowMs;
                _stallCountedForCurrentBuffering = false;
            }
            else if (snapshot.State == VLCState.Buffering &&
                     nowMs >= _postSeekGraceUntilMs &&
                     nowMs - _lastProgressAtMs >= 4500 &&
                     !_stallCountedForCurrentBuffering)
            {
                _stallCountedForCurrentBuffering = true;
                if (nowMs - _stallWindowStartMs > 90_000)
                {
                    _stallEvents = 1;
                    _stallWindowStartMs = nowMs;
                }
                else
                {
                    _stallEvents++;
                }
                if (_stallEvents >= 3)
                {
                    _stallEvents = 0;
                    suggestLowerQuality = true;
                }
            }
        }

        return new TickDecision(effectivePlaying, showBuffering, displayPos, shouldUpdatePos, suggestLowerQuality);
    }

    public static long NowMs() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
}
