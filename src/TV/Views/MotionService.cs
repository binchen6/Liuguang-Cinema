using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;

namespace TV.Views;

public enum PageTransitionKind
{
    TopLevel,
    DrillIn,
    DrillOut,
    Immersive
}

/// <summary>
/// Compositor-thread, physics-driven, fully interruptible motion engine ("Fluid Kinetic Grammar").
/// Every transition animates WinUI Composition Visual properties (Translation / Scale / Opacity),
/// natively preserving momentum and smoothly retargeting if interrupted mid-flight.
/// Strictly respects user preference (MotionService.Enabled) and Windows accessibility settings.
/// </summary>
public static class MotionService
{
    private sealed class MotionState
    {
        public int Token;
        public bool CenterPivotWired;
        public bool TopPivotWired;
        public bool TactileWired;
        public bool IsHovered;
        public bool IsPressed;
        public double LastIndicatorPos = double.NaN;
        public bool BeaconActive;
        public bool RadarActive;
    }

    private static readonly UISettings UiSettings = new();
    private static readonly ConditionalWeakTable<UIElement, MotionState> States = new();
    private static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Changed?.Invoke(null, EventArgs.Empty); }
    }

    public static event EventHandler? Changed;
    public static bool CanAnimate => Enabled && UiSettings.AnimationsEnabled;

    private static MotionState GetState(UIElement element) => States.GetOrCreateValue(element);

    private static Visual PrepareVisual(FrameworkElement element, bool centerPivot = false, bool topCenterPivot = false)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (centerPivot) EnsureCenterPivot(element, visual);
        else if (topCenterPivot) EnsureTopCenterPivot(element, visual);
        return visual;
    }

    private static void EnsureCenterPivot(FrameworkElement element, Visual visual)
    {
        if (element.ActualWidth > 0 && element.ActualHeight > 0)
            visual.CenterPoint = new Vector3((float)(element.ActualWidth * 0.5), (float)(element.ActualHeight * 0.5), 0f);
        var state = GetState(element);
        if (state.CenterPivotWired) return;
        state.CenterPivotWired = true;
        element.SizeChanged += static (sender, args) =>
        {
            if (sender is FrameworkElement fe)
            {
                var v = ElementCompositionPreview.GetElementVisual(fe);
                v.CenterPoint = new Vector3((float)(args.NewSize.Width * 0.5), (float)(args.NewSize.Height * 0.5), 0f);
            }
        };
    }

    private static void EnsureTopCenterPivot(FrameworkElement element, Visual visual)
    {
        if (element.ActualWidth > 0)
            visual.CenterPoint = new Vector3((float)(element.ActualWidth * 0.5), 0f, 0f);
        var state = GetState(element);
        if (state.TopPivotWired) return;
        state.TopPivotWired = true;
        element.SizeChanged += static (sender, args) =>
        {
            if (sender is FrameworkElement fe)
            {
                var v = ElementCompositionPreview.GetElementVisual(fe);
                v.CenterPoint = new Vector3((float)(args.NewSize.Width * 0.5), 0f, 0f);
            }
        };
    }

    // Calibrated Easing Curves
    private static CubicBezierEasingFunction EaseOutExpo(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));

    private static CubicBezierEasingFunction EaseOutQuint(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.0f), new Vector2(0.36f, 1.0f));

    private static CubicBezierEasingFunction EaseInOutSine(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.37f, 0.0f), new Vector2(0.63f, 1.0f));

    private static CubicBezierEasingFunction EaseInCubic(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.32f, 0.0f), new Vector2(0.67f, 0.0f));

    private static SpringVector3NaturalMotionAnimation CreateSpringVector3(
        Compositor compositor,
        Vector3 finalValue,
        float dampingRatio,
        int periodMs,
        Vector3? initialValue = null,
        int delayMs = 0)
    {
        var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = finalValue;
        spring.DampingRatio = dampingRatio;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);
        if (initialValue.HasValue) spring.InitialValue = initialValue.Value;
        if (delayMs > 0)
        {
            spring.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            spring.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        return spring;
    }

    private static ScalarKeyFrameAnimation CreateScalarKeyFrame(
        Compositor compositor,
        float to,
        int durationMs,
        CompositionEasingFunction easing,
        float? from = null,
        int delayMs = 0)
    {
        var anim = compositor.CreateScalarKeyFrameAnimation();
        if (from.HasValue) anim.InsertKeyFrame(0f, from.Value);
        anim.InsertKeyFrame(1f, to, easing);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        if (delayMs > 0)
        {
            anim.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            anim.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
        return anim;
    }

    private static Vector3KeyFrameAnimation CreateVector3KeyFrame(
        Compositor compositor,
        Vector3 to,
        int durationMs,
        CompositionEasingFunction easing,
        Vector3? from = null)
    {
        var anim = compositor.CreateVector3KeyFrameAnimation();
        if (from.HasValue) anim.InsertKeyFrame(0f, from.Value);
        anim.InsertKeyFrame(1f, to, easing);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        return anim;
    }

    /// <summary>
    /// Backward-compatible soft entrance.
    /// </summary>
    public static void Reveal(FrameworkElement element) => EnterSection(element, offsetY: 12);

    /// <summary>
    /// Choreography for page navigation transitions.
    /// Completely interruptible if the user rapidly clicks across navigation tabs.
    /// </summary>
    public static void EnterPage(FrameworkElement element, PageTransitionKind kind)
    {
        var state = GetState(element);
        state.Token++;
        var visual = PrepareVisual(element, centerPivot: true);
        visual.Clip = null;

        if (!CanAnimate)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation("Translation");
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Opacity = 1f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Scale = Vector3.One;
            return;
        }

        var c = visual.Compositor;
        Vector3 startOffset;
        Vector3 startScale;
        int opacityMs;
        int springPeriodMs;
        float damping;

        switch (kind)
        {
            case PageTransitionKind.DrillIn:
                startOffset = new Vector3(30f, 0f, 0f);
                startScale = new Vector3(0.984f, 0.984f, 1f);
                opacityMs = 220;
                springPeriodMs = 44;
                damping = 0.88f;
                break;
            case PageTransitionKind.DrillOut:
                startOffset = new Vector3(-24f, 0f, 0f);
                startScale = new Vector3(1.012f, 1.012f, 1f);
                opacityMs = 200;
                springPeriodMs = 42;
                damping = 0.90f;
                break;
            case PageTransitionKind.Immersive:
                startOffset = Vector3.Zero;
                startScale = new Vector3(0.968f, 0.968f, 1f);
                opacityMs = 250;
                springPeriodMs = 50;
                damping = 0.92f;
                break;
            default: // TopLevel
                startOffset = new Vector3(0f, 14f, 0f);
                startScale = new Vector3(0.992f, 0.992f, 1f);
                opacityMs = 210;
                springPeriodMs = 44;
                damping = 0.90f;
                break;
        }

        visual.StartAnimation(nameof(Visual.Opacity), CreateScalarKeyFrame(c, 1f, opacityMs, EaseOutQuint(c), from: 0f));
        visual.StartAnimation("Translation", CreateSpringVector3(c, Vector3.Zero, damping, springPeriodMs, startOffset));
        visual.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, Vector3.One, damping, springPeriodMs, startScale));
    }

    /// <summary>
    /// Subtle, crisp section switch entrance (Settings sections, Library tabs, Detail switch).
    /// Supports optional horizontal direction for lateral tab switches.
    /// </summary>
    public static void EnterSection(FrameworkElement element, double offsetY = 12, double offsetX = 0)
    {
        var state = GetState(element);
        state.Token++;
        var visual = PrepareVisual(element);
        visual.Clip = null;

        if (!CanAnimate)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation("Translation");
            visual.Opacity = 1f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }

        var c = visual.Compositor;
        visual.StartAnimation(nameof(Visual.Opacity), CreateScalarKeyFrame(c, 1f, 195, EaseOutQuint(c), from: 0f));
        visual.StartAnimation("Translation",
            CreateSpringVector3(c, Vector3.Zero, 0.89f, 40, new Vector3((float)offsetX, (float)offsetY, 0f)));
    }

    /// <summary>
    /// Choreographed editorial cascade across major page bands (e.g., Masthead -> Filter Dock -> Main Grid).
    /// Runs natively on the Compositor via DelayTime with tight cap (<= 105ms) so it feels instantaneous yet layered.
    /// </summary>
    public static void StaggerEntrance(double offsetY = 12, int stepMs = 35, params FrameworkElement?[] elements)
    {
        var idx = 0;
        foreach (var element in elements)
        {
            if (element is null || element.Visibility != Visibility.Visible) continue;
            var state = GetState(element);
            state.Token++;
            var visual = PrepareVisual(element);
            visual.Clip = null;

            if (!CanAnimate)
            {
                visual.StopAnimation(nameof(Visual.Opacity));
                visual.StopAnimation("Translation");
                visual.Opacity = 1f;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                continue;
            }

            var delay = Math.Min(idx * stepMs, 105);
            var c = visual.Compositor;
            visual.StartAnimation(nameof(Visual.Opacity),
                CreateScalarKeyFrame(c, 1f, 210, EaseOutQuint(c), from: 0f, delayMs: delay));
            visual.StartAnimation("Translation",
                CreateSpringVector3(c, Vector3.Zero, 0.90f, 42, new Vector3(0f, (float)offsetY, 0f), delayMs: delay));
            idx++;
        }
    }

    /// <summary>
    /// Elastic spring-physics vertical rail indicator glide (Main sidebar + Settings section rail).
    /// Velocity-inheriting & mid-flight interruptible with subtle travel-proportional stretch pulse.
    /// </summary>
    public static void GlideVerticalIndicator(FrameworkElement indicator, double targetY, bool animate)
    {
        var state = GetState(indicator);
        var visual = PrepareVisual(indicator, centerPivot: true);
        var target = new Vector3(0f, (float)targetY, 0f);
        var prevY = state.LastIndicatorPos;
        state.LastIndicatorPos = targetY;

        if (!animate || !CanAnimate)
        {
            visual.StopAnimation("Translation");
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Properties.InsertVector3("Translation", target);
            visual.Scale = Vector3.One;
            visual.Opacity = 1f;
            return;
        }

        var c = visual.Compositor;
        visual.Opacity = 1f;
        visual.StartAnimation("Translation", CreateSpringVector3(c, target, dampingRatio: 0.76f, periodMs: 36));

        var travel = double.IsNaN(prevY) ? 0d : Math.Abs(targetY - prevY);
        if (travel > 8d)
        {
            var stretchY = (float)Math.Clamp(1.0 + travel / 170.0, 1.14, 1.42);
            var squashX = (float)Math.Clamp(1.0 - (stretchY - 1.0) * 0.25, 0.88, 0.96);
            visual.StartAnimation(nameof(Visual.Scale),
                CreateSpringVector3(c, Vector3.One, dampingRatio: 0.74f, periodMs: 34,
                    initialValue: new Vector3(squashX, stretchY, 1f)));
        }
    }

    /// <summary>
    /// Elastic spring-physics horizontal segmented pill glide (Library segmented track).
    /// Smoothly translates X while preserving true rounded geometry and giving subtle kinetic squeeze.
    /// </summary>
    public static void GlideHorizontalPill(FrameworkElement pill, double targetX, double targetWidth, bool animate)
    {
        var state = GetState(pill);
        var visual = PrepareVisual(pill, centerPivot: true);
        var target = new Vector3((float)targetX, 0f, 0f);
        var prevX = state.LastIndicatorPos;
        state.LastIndicatorPos = targetX;

        if (targetWidth > 0 && Math.Abs(pill.Width - targetWidth) > 0.5)
            pill.Width = targetWidth;

        if (!animate || !CanAnimate)
        {
            visual.StopAnimation("Translation");
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Properties.InsertVector3("Translation", target);
            visual.Scale = Vector3.One;
            visual.Opacity = 1f;
            return;
        }

        var c = visual.Compositor;
        visual.Opacity = 1f;
        visual.StartAnimation("Translation", CreateSpringVector3(c, target, dampingRatio: 0.78f, periodMs: 36));

        var travel = double.IsNaN(prevX) ? 0d : Math.Abs(targetX - prevX);
        if (travel > 12d)
        {
            var stretchX = (float)Math.Clamp(1.0 + travel / 480.0, 1.03, 1.10);
            visual.StartAnimation(nameof(Visual.Scale),
                CreateSpringVector3(c, Vector3.One, dampingRatio: 0.78f, periodMs: 34,
                    initialValue: new Vector3(stretchX, 0.95f, 1f)));
        }
    }

    /// <summary>
    /// Interruptible spring-physics hover lift for poster cards and featured gallery tiles.
    /// </summary>
    public static void SetCardHover(FrameworkElement card, FrameworkElement? playOverlay, FrameworkElement? playBadge, bool hovered)
        => SetCardHover(card, playOverlay, playBadge, null, hovered);

    /// <summary>
    /// Dual-layer depth card hover: card lifts smoothly while the inner clipped artwork performs an
    /// independent micro-zoom (1.055x) and the frosted play medallion springs into focus.
    /// </summary>
    public static void SetCardHover(
        FrameworkElement card,
        FrameworkElement? playOverlay,
        FrameworkElement? playBadge,
        FrameworkElement? artworkZoom,
        bool hovered)
    {
        var state = GetState(card);
        state.IsHovered = hovered;
        var visual = PrepareVisual(card, centerPivot: true);

        if (!CanAnimate)
        {
            visual.StopAnimation(nameof(Visual.Scale));
            visual.StopAnimation("Translation");
            visual.Scale = hovered ? new Vector3(1.02f, 1.02f, 1f) : Vector3.One;
            visual.Properties.InsertVector3("Translation", hovered ? new Vector3(0f, -3f, 0f) : Vector3.Zero);
            if (artworkZoom is not null)
            {
                var av = PrepareVisual(artworkZoom, centerPivot: true);
                av.StopAnimation(nameof(Visual.Scale));
                av.Scale = Vector3.One;
            }
            if (playOverlay is not null)
            {
                var ov = ElementCompositionPreview.GetElementVisual(playOverlay);
                ov.StopAnimation(nameof(Visual.Opacity));
                ov.Opacity = hovered ? 1f : 0f;
                playOverlay.Opacity = hovered ? 1.0 : 0.0;
            }
            return;
        }

        var c = visual.Compositor;
        var targetScale = hovered ? new Vector3(1.022f, 1.022f, 1f) : Vector3.One;
        var targetLift = hovered ? new Vector3(0f, -4f, 0f) : Vector3.Zero;
        visual.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, targetScale, 0.80f, 36));
        visual.StartAnimation("Translation", CreateSpringVector3(c, targetLift, 0.82f, 36));

        if (artworkZoom is not null)
        {
            var av = PrepareVisual(artworkZoom, centerPivot: true);
            var artScale = hovered ? new Vector3(1.055f, 1.055f, 1f) : Vector3.One;
            av.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, artScale, 0.86f, 44));
        }

        if (playOverlay is not null)
        {
            playOverlay.Opacity = 1.0;
            var ov = ElementCompositionPreview.GetElementVisual(playOverlay);
            ov.StartAnimation(nameof(Visual.Opacity),
                CreateScalarKeyFrame(c, hovered ? 1f : 0f, hovered ? 155 : 120, hovered ? EaseOutQuint(c) : EaseInCubic(c)));
        }

        if (playBadge is not null)
        {
            var bv = PrepareVisual(playBadge, centerPivot: true);
            var badgeScale = hovered ? Vector3.One : new Vector3(0.76f, 0.76f, 1f);
            bv.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, badgeScale, 0.74f, 32));
        }
    }

    /// <summary>
    /// Immediately resets a recycled virtualized card container so hover/press visuals never leak onto newly realized items.
    /// </summary>
    public static void ResetCard(
        FrameworkElement card,
        FrameworkElement? playOverlay = null,
        FrameworkElement? playBadge = null,
        FrameworkElement? artworkZoom = null)
    {
        var state = GetState(card);
        state.IsHovered = false;
        state.IsPressed = false;
        var visual = PrepareVisual(card, centerPivot: true);
        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation("Translation");
        visual.Scale = Vector3.One;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);

        if (artworkZoom is not null)
        {
            var av = PrepareVisual(artworkZoom, centerPivot: true);
            av.StopAnimation(nameof(Visual.Scale));
            av.Scale = Vector3.One;
        }
        if (playOverlay is not null)
        {
            var ov = ElementCompositionPreview.GetElementVisual(playOverlay);
            ov.StopAnimation(nameof(Visual.Opacity));
            ov.Opacity = 0f;
            playOverlay.Opacity = 0.0;
        }
        if (playBadge is not null)
        {
            var bv = PrepareVisual(playBadge, centerPivot: true);
            bv.StopAnimation(nameof(Visual.Scale));
            bv.Scale = new Vector3(0.76f, 0.76f, 1f);
        }
    }

    /// <summary>
    /// Smoothly cross-fades an element's Composition opacity (e.g. sticky frosted dock backdrop or active plate).
    /// Interruptible mid-flight and respects CanAnimate.
    /// </summary>
    public static void FadeOpacity(FrameworkElement element, float targetOpacity, int durationMs = 180)
    {
        var visual = PrepareVisual(element);
        if (!CanAnimate)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = targetOpacity;
            element.Opacity = targetOpacity;
            return;
        }
        element.Opacity = 1.0;
        var c = visual.Compositor;
        visual.StartAnimation(nameof(Visual.Opacity),
            CreateScalarKeyFrame(c, targetOpacity, durationMs, targetOpacity > 0.5f ? EaseOutQuint(c) : EaseInCubic(c)));
    }

    /// <summary>
    /// Tactile press recoil for cards; smoothly rebounds to hover or rest state on release.
    /// </summary>
    public static void SetCardPressed(FrameworkElement card, bool pressed)
    {
        if (!CanAnimate) return;
        var state = GetState(card);
        state.IsPressed = pressed;
        var visual = PrepareVisual(card, centerPivot: true);
        var c = visual.Compositor;
        var targetScale = pressed
            ? new Vector3(0.978f, 0.978f, 1f)
            : state.IsHovered ? new Vector3(1.022f, 1.022f, 1f) : Vector3.One;
        var targetLift = pressed
            ? new Vector3(0f, -1f, 0f)
            : state.IsHovered ? new Vector3(0f, -4f, 0f) : Vector3.Zero;
        visual.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, targetScale, 0.84f, 28));
        visual.StartAnimation("Translation", CreateSpringVector3(c, targetLift, 0.86f, 28));
    }

    /// <summary>
    /// Attaches compositor-thread tactile hover/press physics to any key interactive control
    /// (CTAs, FABs, icon medallions, episode tiles) without interfering with XAML visual states.
    /// Safe to call multiple times (idempotent).
    /// </summary>
    public static void AttachTactile(FrameworkElement element, float hoverScale = 1.025f, float pressScale = 0.962f, float hoverLiftY = -1.5f)
    {
        var state = GetState(element);
        if (state.TactileWired) return;
        state.TactileWired = true;

        void UpdatePose()
        {
            var visual = PrepareVisual(element, centerPivot: true);
            var disabled = element is Control ctrl && !ctrl.IsEnabled;
            if (!CanAnimate || disabled)
            {
                visual.StopAnimation(nameof(Visual.Scale));
                visual.StopAnimation("Translation");
                visual.Scale = Vector3.One;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                return;
            }
            var c = visual.Compositor;
            var s = state.IsPressed ? pressScale : state.IsHovered ? hoverScale : 1f;
            var y = state.IsPressed ? 0.5f : state.IsHovered ? hoverLiftY : 0f;
            var period = state.IsPressed ? 26 : 34;
            visual.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, new Vector3(s, s, 1f), 0.82f, period));
            visual.StartAnimation("Translation", CreateSpringVector3(c, new Vector3(0f, y, 0f), 0.84f, period));
        }

        element.PointerEntered += (_, _) => { state.IsHovered = true; UpdatePose(); };
        element.PointerExited += (_, _) => { state.IsHovered = false; state.IsPressed = false; UpdatePose(); };
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { state.IsPressed = true; UpdatePose(); }), handledEventsToo: true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { state.IsPressed = false; UpdatePose(); }), handledEventsToo: true);
        element.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => { state.IsPressed = false; state.IsHovered = false; UpdatePose(); }), handledEventsToo: true);
    }

    /// <summary>
    /// Instant kinetic spring pulse for immediate tactile acknowledgment (e.g., Play/Pause toggle, Favorite, OSD re-bump).
    /// </summary>
    public static void PulseElement(FrameworkElement element, float impulseScale = 0.89f)
    {
        if (!CanAnimate) return;
        var visual = PrepareVisual(element, centerPivot: true);
        var c = visual.Compositor;
        visual.StartAnimation(nameof(Visual.Scale),
            CreateSpringVector3(c, Vector3.One, dampingRatio: 0.72f, periodMs: 30,
                initialValue: new Vector3(impulseScale, impulseScale, 1f)));
    }

    /// <summary>
    /// Interruptible show/hide for floating UI elements (BackToTop FAB, CenterPlayButton, BackButton, HUD).
    /// Safe under rapid toggling: stale hide completions never collapse a newly shown element.
    /// </summary>
    public static void SetFloatingVisible(
        FrameworkElement element,
        bool visible,
        double offsetY = 10,
        double offsetX = 0,
        float hiddenScale = 0.92f,
        bool collapseWhenHidden = true)
    {
        var state = GetState(element);
        var token = ++state.Token;
        var visual = PrepareVisual(element, centerPivot: true);

        if (visible)
        {
            element.Visibility = Visibility.Visible;
            element.IsHitTestVisible = true;
        }
        else
        {
            element.IsHitTestVisible = false;
        }

        if (!CanAnimate)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation("Translation");
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Opacity = visible ? 1f : 0f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Scale = Vector3.One;
            if (!visible && collapseWhenHidden) element.Visibility = Visibility.Collapsed;
            return;
        }

        var c = visual.Compositor;
        if (visible)
        {
            var wasHidden = visual.Opacity <= 0.02f;
            visual.StartAnimation(nameof(Visual.Opacity),
                CreateScalarKeyFrame(c, 1f, 175, EaseOutExpo(c), wasHidden ? 0f : null));
            visual.StartAnimation("Translation",
                CreateSpringVector3(c, Vector3.Zero, 0.82f, 34,
                    wasHidden ? new Vector3((float)offsetX, (float)offsetY, 0f) : null));
            visual.StartAnimation(nameof(Visual.Scale),
                CreateSpringVector3(c, Vector3.One, 0.80f, 34,
                    wasHidden ? new Vector3(hiddenScale, hiddenScale, 1f) : null));
        }
        else
        {
            var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation(nameof(Visual.Opacity), CreateScalarKeyFrame(c, 0f, 150, EaseInCubic(c)));
            visual.StartAnimation("Translation",
                CreateVector3KeyFrame(c, new Vector3((float)offsetX, (float)offsetY, 0f), 150, EaseInCubic(c)));
            visual.StartAnimation(nameof(Visual.Scale),
                CreateVector3KeyFrame(c, new Vector3(hiddenScale, hiddenScale, 1f), 150, EaseInCubic(c)));
            batch.End();
            if (collapseWhenHidden)
            {
                batch.Completed += (_, _) =>
                {
                    if (state.Token == token)
                        element.Visibility = Visibility.Collapsed;
                    batch.Dispose();
                };
            }
            else
            {
                batch.Completed += (_, _) => batch.Dispose();
            }
        }
    }

    /// <summary>
    /// Top-anchored frosted glass dropdown / suggestion overlay unfurl & fold.
    /// Completely interruptible if user resumes typing mid-dismissal.
    /// </summary>
    public static void SetOverlayVisible(FrameworkElement overlay, bool visible)
    {
        var state = GetState(overlay);
        var token = ++state.Token;
        var visual = PrepareVisual(overlay, topCenterPivot: true);

        if (visible)
        {
            overlay.Visibility = Visibility.Visible;
            overlay.IsHitTestVisible = true;
        }
        else
        {
            overlay.IsHitTestVisible = false;
        }

        if (!CanAnimate)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation("Translation");
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Opacity = visible ? 1f : 0f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Scale = Vector3.One;
            if (!visible) overlay.Visibility = Visibility.Collapsed;
            return;
        }

        var c = visual.Compositor;
        if (visible)
        {
            var wasHidden = visual.Opacity <= 0.04f;
            visual.StartAnimation(nameof(Visual.Opacity),
                CreateScalarKeyFrame(c, 1f, 155, EaseOutQuint(c), wasHidden ? 0f : null));
            visual.StartAnimation("Translation",
                CreateSpringVector3(c, Vector3.Zero, 0.86f, 34, wasHidden ? new Vector3(0f, -8f, 0f) : null));
            visual.StartAnimation(nameof(Visual.Scale),
                CreateSpringVector3(c, Vector3.One, 0.86f, 34, wasHidden ? new Vector3(0.975f, 0.96f, 1f) : null));
        }
        else
        {
            var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation(nameof(Visual.Opacity), CreateScalarKeyFrame(c, 0f, 125, EaseInCubic(c)));
            visual.StartAnimation("Translation", CreateVector3KeyFrame(c, new Vector3(0f, -6f, 0f), 125, EaseInCubic(c)));
            visual.StartAnimation(nameof(Visual.Scale), CreateVector3KeyFrame(c, new Vector3(0.98f, 0.97f, 1f), 125, EaseInCubic(c)));
            batch.End();
            batch.Completed += (_, _) =>
            {
                if (state.Token == token)
                    overlay.Visibility = Visibility.Collapsed;
                batch.Dispose();
            };
        }
    }

    /// <summary>
    /// Interruptible OSD toast with live re-trigger micro-breath (so repeated arrow-key seeks feel crisp, never blinking).
    /// </summary>
    public static void ShowOsdToast(FrameworkElement badge)
    {
        var state = GetState(badge);
        state.Token++;
        var visual = PrepareVisual(badge, centerPivot: true);
        var alreadyVisible = badge.Visibility == Visibility.Visible && visual.Opacity > 0.65f;
        badge.Visibility = Visibility.Visible;

        if (!CanAnimate)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation("Translation");
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Opacity = 1f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            visual.Scale = Vector3.One;
            return;
        }

        var c = visual.Compositor;
        if (alreadyVisible)
        {
            visual.StartAnimation(nameof(Visual.Opacity), CreateScalarKeyFrame(c, 1f, 90, EaseOutQuint(c)));
            visual.StartAnimation("Translation", CreateSpringVector3(c, Vector3.Zero, 0.84f, 30));
            visual.StartAnimation(nameof(Visual.Scale),
                CreateSpringVector3(c, Vector3.One, 0.78f, 28, new Vector3(1.045f, 1.045f, 1f)));
        }
        else
        {
            visual.StartAnimation(nameof(Visual.Opacity),
                CreateScalarKeyFrame(c, 1f, 155, EaseOutExpo(c), visual.Opacity <= 0.04f ? 0f : null));
            visual.StartAnimation("Translation",
                CreateSpringVector3(c, Vector3.Zero, 0.82f, 34, visual.Opacity <= 0.04f ? new Vector3(0f, -10f, 0f) : null));
            visual.StartAnimation(nameof(Visual.Scale),
                CreateSpringVector3(c, Vector3.One, 0.80f, 34, visual.Opacity <= 0.04f ? new Vector3(0.92f, 0.92f, 1f) : null));
        }
    }

    public static void HideOsdToast(FrameworkElement badge) =>
        SetFloatingVisible(badge, false, offsetY: -8, hiddenScale: 0.94f, collapseWhenHidden: true);

    /// <summary>
    /// Interruptible cinema player chrome choreography: top bar glides from above,
    /// floating frosted bottom dock glides smoothly up with subtle scale settle.
    /// Seamlessly reverses mid-flight whenever pointer or keyboard wakes controls.
    /// </summary>
    public static void SetPlayerChromeVisible(
        FrameworkElement controlsLayer,
        FrameworkElement? topBar,
        FrameworkElement? bottomBar,
        bool visible)
    {
        var state = GetState(controlsLayer);
        state.Token++;
        controlsLayer.IsHitTestVisible = visible;
        var layerVisual = PrepareVisual(controlsLayer);
        var topVisual = topBar is not null ? PrepareVisual(topBar) : null;
        var bottomVisual = bottomBar is not null ? PrepareVisual(bottomBar, centerPivot: true) : null;

        if (!CanAnimate)
        {
            layerVisual.StopAnimation(nameof(Visual.Opacity));
            layerVisual.Opacity = visible ? 1f : 0f;
            if (topVisual is not null)
            {
                topVisual.StopAnimation("Translation");
                topVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            if (bottomVisual is not null)
            {
                bottomVisual.StopAnimation("Translation");
                bottomVisual.StopAnimation(nameof(Visual.Scale));
                bottomVisual.Properties.InsertVector3("Translation", Vector3.Zero);
                bottomVisual.Scale = Vector3.One;
            }
            return;
        }

        var c = layerVisual.Compositor;
        layerVisual.StartAnimation(nameof(Visual.Opacity),
            CreateScalarKeyFrame(c, visible ? 1f : 0f, visible ? 185 : 220, visible ? EaseOutQuint(c) : EaseInCubic(c)));

        if (topVisual is not null)
        {
            if (visible)
                topVisual.StartAnimation("Translation", CreateSpringVector3(c, Vector3.Zero, 0.88f, 38));
            else
                topVisual.StartAnimation("Translation",
                    CreateVector3KeyFrame(c, new Vector3(0f, -20f, 0f), 200, EaseInCubic(c)));
        }

        if (bottomVisual is not null)
        {
            if (visible)
            {
                bottomVisual.StartAnimation("Translation", CreateSpringVector3(c, Vector3.Zero, 0.86f, 40));
                bottomVisual.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, Vector3.One, 0.88f, 40));
            }
            else
            {
                bottomVisual.StartAnimation("Translation",
                    CreateVector3KeyFrame(c, new Vector3(0f, 26f, 0f), 210, EaseInCubic(c)));
                bottomVisual.StartAnimation(nameof(Visual.Scale),
                    CreateVector3KeyFrame(c, new Vector3(0.982f, 0.982f, 1f), 210, EaseInCubic(c)));
            }
        }
    }

    /// <summary>
    /// Compositor-thread ambient breathing beacon for LIVE ON AIR and Cast status indicators.
    /// Zero UI-thread timers; halts cleanly when inactive or when animations are disabled.
    /// </summary>
    public static void SetBeaconPulsing(FrameworkElement beacon, bool active)
    {
        var state = GetState(beacon);
        var visual = PrepareVisual(beacon, centerPivot: true);

        if (!active || !CanAnimate)
        {
            state.BeaconActive = false;
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Opacity = active ? 1f : 0.35f;
            visual.Scale = Vector3.One;
            return;
        }

        if (state.BeaconActive) return;
        state.BeaconActive = true;

        var c = visual.Compositor;
        var sine = EaseInOutSine(c);

        var op = c.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0f, 1f);
        op.InsertKeyFrame(0.5f, 0.38f, sine);
        op.InsertKeyFrame(1f, 1f, sine);
        op.Duration = TimeSpan.FromMilliseconds(2200);
        op.IterationBehavior = AnimationIterationBehavior.Forever;

        var sc = c.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0f, new Vector3(1.08f, 1.08f, 1f));
        sc.InsertKeyFrame(0.5f, new Vector3(0.88f, 0.88f, 1f), sine);
        sc.InsertKeyFrame(1f, new Vector3(1.08f, 1.08f, 1f), sine);
        sc.Duration = TimeSpan.FromMilliseconds(2200);
        sc.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation(nameof(Visual.Opacity), op);
        visual.StartAnimation(nameof(Visual.Scale), sc);
    }

    /// <summary>
    /// Compositor-thread concentric radar wave pulse for the Cast Receiver waiting stage.
    /// </summary>
    public static void SetRadarRingPulsing(FrameworkElement ring, bool active, int delayMs = 0)
    {
        var state = GetState(ring);
        var visual = PrepareVisual(ring, centerPivot: true);

        if (!active || !CanAnimate)
        {
            state.RadarActive = false;
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Opacity = 0f;
            visual.Scale = Vector3.One;
            return;
        }

        if (state.RadarActive) return;
        state.RadarActive = true;

        var c = visual.Compositor;
        var outQuint = EaseOutQuint(c);

        var op = c.CreateScalarKeyFrameAnimation();
        op.InsertKeyFrame(0f, 0.42f);
        op.InsertKeyFrame(0.75f, 0.10f, outQuint);
        op.InsertKeyFrame(1f, 0f, outQuint);
        op.Duration = TimeSpan.FromMilliseconds(2800);
        op.IterationBehavior = AnimationIterationBehavior.Forever;
        if (delayMs > 0)
        {
            op.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            op.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        var sc = c.CreateVector3KeyFrameAnimation();
        sc.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1f));
        sc.InsertKeyFrame(1f, new Vector3(1.55f, 1.55f, 1f), outQuint);
        sc.Duration = TimeSpan.FromMilliseconds(2800);
        sc.IterationBehavior = AnimationIterationBehavior.Forever;
        if (delayMs > 0)
        {
            sc.DelayTime = TimeSpan.FromMilliseconds(delayMs);
            sc.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        visual.StartAnimation(nameof(Visual.Opacity), op);
        visual.StartAnimation(nameof(Visual.Scale), sc);
    }

    /// <summary>
    /// GPU-driven scroll-linked parallax for atmospheric hero backdrops (zero UI-thread overhead).
    /// </summary>
    public static void BindCoverParallax(ScrollViewer scrollViewer, FrameworkElement backdrop, float factor = 0.22f)
    {
        var visual = PrepareVisual(backdrop);
        if (!CanAnimate)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }
        var scrollProps = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scrollViewer);
        var c = visual.Compositor;
        var expr = c.CreateExpressionAnimation($"Vector3(0.0, Clamp(-Scroll.Translation.Y * {factor.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 0.0, 56.0), 0.0)");
        expr.SetReferenceParameter("Scroll", scrollProps);
        visual.StartAnimation("Translation", expr);
    }

    public static void BindProgressiveBlur(ScrollViewer scrollViewer, FrameworkElement header)
    {
        if (!CanAnimate) return;
        var scrollProperties = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(scrollViewer);
        var visual = ElementCompositionPreview.GetElementVisual(header);
        var compositor = visual.Compositor;
        var opacity = compositor.CreateExpressionAnimation("Clamp(1.0 + Scroll.Translation.Y / 180.0, 0.35, 1.0)");
        opacity.SetReferenceParameter("Scroll", scrollProperties);
        visual.StartAnimation(nameof(visual.Opacity), opacity);
        var parallax = compositor.CreateExpressionAnimation("Vector3(0.0, Clamp(-Scroll.Translation.Y * 0.08, 0.0, 14.0), 0.0)");
        parallax.SetReferenceParameter("Scroll", scrollProperties);
        visual.StartAnimation(nameof(visual.Offset), parallax);
    }
}
