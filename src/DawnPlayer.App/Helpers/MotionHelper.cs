using DawnPlayer.App.Services;
using DawnPlayer.App.Styles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace DawnPlayer.App.Helpers;

/// <summary>
/// U1 motion primitives. Every entry point takes the motion gate result and, when motion is
/// off, sets the final state instantly — "reduced motion ⇒ zero non-essential animation" is
/// enforced by construction, not by each call site remembering to branch. Durations come from
/// the design tokens; only opacity/transform are animated (compositor-friendly, no layout).
/// </summary>
public static class MotionHelper
{
    /// <summary>Fades an element in (tab switches, wallpaper, cover art). End state is fully
    /// opaque in both paths.</summary>
    public static void FadeIn(FrameworkElement? element, bool motionEnabled)
    {
        if (element == null) return;
        if (!motionEnabled)
        {
            element.Opacity = 1;
            return;
        }

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(DesignTokenValues.Motion.NormalMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    /// <summary>Quick press "pop": scale down then settle back. Transform-only; when motion is
    /// off nothing runs. Safe to call repeatedly (target transform is ensured first).</summary>
    public static void PressPop(FrameworkElement? element, bool motionEnabled)
    {
        if (element == null || !motionEnabled) return;

        var scale = element.RenderTransform as ScaleTransform;
        if (scale == null)
        {
            scale = new ScaleTransform { CenterX = 0.5, CenterY = 0.5 };
            element.RenderTransform = scale;
            // RenderTransformOrigin drives the transform from the element's center even though
            // ScaleTransform centers on its own coordinates here.
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            scale.CenterX = 0;
            scale.CenterY = 0;
        }

        var storyboard = new Storyboard();
        var down = new DoubleAnimationUsingKeyFrames();
        down.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DesignTokenValues.Motion.FastMs)),
            Value = 0.94,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        down.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DesignTokenValues.Motion.NormalMs)),
            Value = 1.0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTarget(down, element);
        Storyboard.SetTargetProperty(down, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
        storyboard.Children.Add(down);

        var storyboardY = new Storyboard();
        var downY = new DoubleAnimationUsingKeyFrames();
        downY.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DesignTokenValues.Motion.FastMs)),
            Value = 0.94,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        downY.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(DesignTokenValues.Motion.NormalMs)),
            Value = 1.0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTarget(downY, element);
        Storyboard.SetTargetProperty(downY, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
        storyboardY.Children.Add(downY);

        storyboard.Begin();
        storyboardY.Begin();
    }
}
