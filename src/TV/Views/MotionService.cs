using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
/// Compositor-thread, physics-driven, fully interruptible motion engine.
/// Every transition animates WinUI Composition Visual properties (Translation / Scale / Opacity),
/// natively preserving momentum and smoothly retargeting if interrupted mid-flight.
/// Strictly respects user preference (MotionService.Enabled) and Windows accessibility settings.
/// </summary>
public static class MotionService
{
    private sealed class MotionState
    {
        public int Token;
        public bool PivotWired;
        public bool IsHovered;
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

    private static Visual PrepareVisual(FrameworkElement element, bool centerPivot = false)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (centerPivot) EnsureCenterPivot(element, visual);
        return visual;
    }

    private static void EnsureCenterPivot(FrameworkElement element, Visual visual)
    {
        if (element.ActualWidth > 0 && element.ActualHeight > 0)
            visual.CenterPoint = new Vector3((float)(element.ActualWidth * 0.5), (float)(element.ActualHeight * 0.5), 0f);
        var state = GetState(element);
        if (state.PivotWired) return;
        state.PivotWired = true;
        element.SizeChanged += static (sender, args) =>
        {
            if (sender is FrameworkElement fe)
            {
                var v = ElementCompositionPreview.GetElementVisual(fe);
                v.CenterPoint = new Vector3((float)(args.NewSize.Width * 0.5), (float)(args.NewSize.Height * 0.5), 0f);
            }
        };
    }

    private static CubicBezierEasingFunction EaseOutExpo(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1.0f), new Vector2(0.30f, 1.0f));

    private static CubicBezierEasingFunction EaseOutQuint(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.0f), new Vector2(0.36f, 1.0f));

    private static CubicBezierEasingFunction EaseInCubic(Compositor c) =>
        c.CreateCubicBezierEasingFunction(new Vector2(0.32f, 0.0f), new Vector2(0.67f, 0.0f));

    private static SpringVector3NaturalMotionAnimation CreateSpringVector3(
        Compositor compositor, Vector3 finalValue, float dampingRatio, int periodMs, Vector3? initialValue = null)
    {
        var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = finalValue;
        spring.DampingRatio = dampingRatio;
        spring.Period = TimeSpan.FromMilliseconds(periodMs);
        if (initialValue.HasValue) spring.InitialValue = initialValue.Value;
        return spring;
    }

    private static ScalarKeyFrameAnimation CreateScalarKeyFrame(
        Compositor compositor, float to, int durationMs, CompositionEasingFunction easing, float? from = null)
    {
        var anim = compositor.CreateScalarKeyFrameAnimation();
        if (from.HasValue) anim.InsertKeyFrame(0f, from.Value);
        anim.InsertKeyFrame(1f, to, easing);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        return anim;
    }

    private static Vector3KeyFrameAnimation CreateVector3KeyFrame(
        Compositor compositor, Vector3 to, int durationMs, CompositionEasingFunction easing, Vector3? from = null)
    {
        var anim = compositor.CreateVector3KeyFrameAnimation();
        if (from.HasValue) anim.InsertKeyFrame(0f, from.Value);
        anim.InsertKeyFrame(1f, to, easing);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        return anim;
    }

    /// <summary>
    /// Backward-compatible soft entrance (replaces the old linear InsetClip wipe).
    /// </summary>
    public static void Reveal(FrameworkElement element) => EnterSection(element, offsetY: 10);

    /// <summary>
    /// Choreography for page navigation transitions (replaces rigid Frame SlideNavigationTransitionInfo).
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
                startOffset = new Vector3(26f, 0f, 0f);
                startScale = new Vector3(0.985f, 0.985f, 1f);
                opacityMs = 220;
                springPeriodMs = 46;
                damping = 0.88f;
                break;
            case PageTransitionKind.DrillOut:
                startOffset = new Vector3(-22f, 0f, 0f);
                startScale = new Vector3(1.012f, 1.012f, 1f);
                opacityMs = 200;
                springPeriodMs = 44;
                damping = 0.90f;
                break;
            case PageTransitionKind.Immersive:
                startOffset = Vector3.Zero;
                startScale = new Vector3(0.97f, 0.97f, 1f);
                opacityMs = 260;
                springPeriodMs = 52;
                damping = 0.92f;
                break;
            default: // TopLevel
                startOffset = new Vector3(0f, 12f, 0f);
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
    /// </summary>
    public static void EnterSection(FrameworkElement element, double offsetY = 10)
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
        visual.StartAnimation(nameof(Visual.Opacity), CreateScalarKeyFrame(c, 1f, 190, EaseOutQuint(c), from: 0f));
        visual.StartAnimation("Translation",
            CreateSpringVector3(c, Vector3.Zero, 0.90f, 40, new Vector3(0f, (float)offsetY, 0f)));
    }

    /// <summary>
    /// Spring-physics floating sidebar navigation indicator glide.
    /// Interruptible mid-flight: rapid tab switching redirects smoothly with velocity inheritance.
    /// </summary>
    public static void GlideVerticalIndicator(FrameworkElement indicator, double targetY, bool animate)
    {
        var visual = PrepareVisual(indicator);
        var target = new Vector3(0f, (float)targetY, 0f);
        if (!animate || !CanAnimate)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", target);
            visual.Opacity = 1f;
            return;
        }

        var c = visual.Compositor;
        visual.Opacity = 1f;
        visual.StartAnimation("Translation", CreateSpringVector3(c, target, dampingRatio: 0.78f, periodMs: 36));
    }

    /// <summary>
    /// Interruptible spring-physics hover lift for poster cards and featured gallery tiles.
    /// </summary>
    public static void SetCardHover(FrameworkElement card, FrameworkElement? playOverlay, FrameworkElement? playBadge, bool hovered)
    {
        var state = GetState(card);
        state.IsHovered = hovered;
        var visual = PrepareVisual(card, centerPivot: true);

        if (!CanAnimate)
        {
            visual.StopAnimation(nameof(Visual.Scale));
            visual.StopAnimation("Translation");
            visual.Scale = hovered ? new Vector3(1.025f, 1.025f, 1f) : Vector3.One;
            visual.Properties.InsertVector3("Translation", hovered ? new Vector3(0f, -3f, 0f) : Vector3.Zero);
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
        var targetScale = hovered ? new Vector3(1.032f, 1.032f, 1f) : Vector3.One;
        var targetLift = hovered ? new Vector3(0f, -4f, 0f) : Vector3.Zero;
        visual.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, targetScale, 0.80f, 36));
        visual.StartAnimation("Translation", CreateSpringVector3(c, targetLift, 0.82f, 36));

        if (playOverlay is not null)
        {
            playOverlay.Opacity = 1.0; // Let Composition visual own opacity cleanly without double multiplication
            var ov = ElementCompositionPreview.GetElementVisual(playOverlay);
            ov.StartAnimation(nameof(Visual.Opacity),
                CreateScalarKeyFrame(c, hovered ? 1f : 0f, hovered ? 150 : 120, hovered ? EaseOutQuint(c) : EaseInCubic(c)));
        }

        if (playBadge is not null)
        {
            var bv = PrepareVisual(playBadge, centerPivot: true);
            var badgeScale = hovered ? Vector3.One : new Vector3(0.80f, 0.80f, 1f);
            bv.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, badgeScale, 0.72f, 32));
        }
    }

    /// <summary>
    /// Tactile press recoil for cards; smoothly rebounds to hover or rest state on release.
    /// </summary>
    public static void SetCardPressed(FrameworkElement card, bool pressed)
    {
        if (!CanAnimate) return;
        var state = GetState(card);
        var visual = PrepareVisual(card, centerPivot: true);
        var c = visual.Compositor;
        var targetScale = pressed
            ? new Vector3(0.978f, 0.978f, 1f)
            : state.IsHovered ? new Vector3(1.032f, 1.032f, 1f) : Vector3.One;
        var targetLift = pressed
            ? new Vector3(0f, -1f, 0f)
            : state.IsHovered ? new Vector3(0f, -4f, 0f) : Vector3.Zero;
        visual.StartAnimation(nameof(Visual.Scale), CreateSpringVector3(c, targetScale, 0.84f, 28));
        visual.StartAnimation("Translation", CreateSpringVector3(c, targetLift, 0.86f, 28));
    }

    /// <summary>
    /// Interruptible show/hide for floating UI elements (BackToTop FAB, OSD badge, CenterPlayButton, BackButton, HUD).
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
            // If element was collapsed/zero opacity, prime starting pose; if mid-hide, smoothly retarget from current pose!
            var wasHidden = visual.Opacity <= 0.02f;
            visual.StartAnimation(nameof(Visual.Opacity),
                CreateScalarKeyFrame(c, 1f, 170, EaseOutExpo(c), wasHidden ? 0f : null));
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
    /// Interruptible cinema player chrome choreography: top bar glides down/up, bottom bar glides up/down,
    /// seamlessly reversing mid-flight whenever pointer or keyboard wakes the controls.
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
        var bottomVisual = bottomBar is not null ? PrepareVisual(bottomBar) : null;

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
                bottomVisual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            return;
        }

        var c = layerVisual.Compositor;
        layerVisual.StartAnimation(nameof(Visual.Opacity),
            CreateScalarKeyFrame(c, visible ? 1f : 0f, visible ? 180 : 210, visible ? EaseOutQuint(c) : EaseInCubic(c)));

        if (topVisual is not null)
        {
            if (visible)
                topVisual.StartAnimation("Translation", CreateSpringVector3(c, Vector3.Zero, 0.88f, 38));
            else
                topVisual.StartAnimation("Translation",
                    CreateVector3KeyFrame(c, new Vector3(0f, -18f, 0f), 200, EaseInCubic(c)));
        }

        if (bottomVisual is not null)
        {
            if (visible)
                bottomVisual.StartAnimation("Translation", CreateSpringVector3(c, Vector3.Zero, 0.88f, 38));
            else
                bottomVisual.StartAnimation("Translation",
                    CreateVector3KeyFrame(c, new Vector3(0f, 22f, 0f), 200, EaseInCubic(c)));
        }
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
