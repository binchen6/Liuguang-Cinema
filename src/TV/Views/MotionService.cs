using System.Numerics;
 using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace TV.Views;

public static class MotionService
{
    private static readonly UISettings UiSettings = new();
    private static bool _enabled = true;
    public static bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Changed?.Invoke(null, EventArgs.Empty); }
    }
    public static event EventHandler? Changed;
    public static bool CanAnimate => Enabled && UiSettings.AnimationsEnabled;

    public static void Reveal(FrameworkElement element)
    {
        if (!CanAnimate || element.ActualWidth <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var clip = compositor.CreateInsetClip(0, 0, 0, 0);
        visual.Clip = clip;
        clip.RightInset = (float)element.ActualWidth;
        var reveal = compositor.CreateScalarKeyFrameAnimation();
        reveal.InsertKeyFrame(0, (float)element.ActualWidth);
        reveal.InsertKeyFrame(1, 0);
        reveal.Duration = TimeSpan.FromMilliseconds(240);
        clip.StartAnimation(nameof(InsetClip.RightInset), reveal);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.35f);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(240);
        visual.StartAnimation(nameof(visual.Opacity), fade);
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
