using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DawnPlayer.App.Helpers;

/// <summary>
/// Reusable controller for interactive mouse drag resizing of sidebars and panels with bounds clamping,
/// cursor shape updates, accent line highlighting, and settings persistence callback.
/// </summary>
public sealed class SplitterResizer
{
    private readonly UIElement _referenceContainer;
    private readonly Rectangle? _highlightLine;
    private readonly double _minWidth;
    private readonly double _maxWidth;
    private readonly bool _invertDelta;
    private readonly Func<double> _getCurrentWidth;
    private readonly Action<double> _setWidth;
    private readonly Action<InputCursor?>? _setCursor;
    private readonly Action<double>? _onCompleted;

    private bool _isDragging;
    private double _dragStartX;
    private double _initialWidth;

    public bool IsDragging => _isDragging;

    public SplitterResizer(
        UIElement referenceContainer,
        Rectangle? highlightLine,
        double minWidth,
        double maxWidth,
        bool invertDelta,
        Func<double> getCurrentWidth,
        Action<double> setWidth,
        Action<InputCursor?>? setCursor = null,
        Action<double>? onCompleted = null)
    {
        _referenceContainer = referenceContainer;
        _highlightLine = highlightLine;
        _minWidth = minWidth;
        _maxWidth = maxWidth;
        _invertDelta = invertDelta;
        _getCurrentWidth = getCurrentWidth;
        _setWidth = setWidth;
        _setCursor = setCursor;
        _onCompleted = onCompleted;
    }

    public void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _setCursor?.Invoke(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
        if (_highlightLine != null)
        {
            _highlightLine.Fill = ThemeResourceHelper.GetBrush("DawnAccentTextBrush");
        }
    }

    public void OnPointerExited(object sender, PointerRoutedEventArgs e, Func<bool> anyOtherDragging)
    {
        if (!_isDragging && !anyOtherDragging())
        {
            _setCursor?.Invoke(null);
            if (_highlightLine != null)
            {
                _highlightLine.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }
    }

    public void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el)
        {
            _isDragging = true;
            _dragStartX = e.GetCurrentPoint(_referenceContainer).Position.X;
            _initialWidth = _getCurrentWidth();
            el.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    public void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            double curX = e.GetCurrentPoint(_referenceContainer).Position.X;
            double delta = curX - _dragStartX;
            double targetWidth = _invertDelta ? (_initialWidth - delta) : (_initialWidth + delta);
            double clampedWidth = Math.Clamp(targetWidth, _minWidth, _maxWidth);
            _setWidth(clampedWidth);
            e.Handled = true;
        }
    }

    public void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging && sender is UIElement el)
        {
            _isDragging = false;
            el.ReleasePointerCapture(e.Pointer);
            if (_highlightLine != null)
            {
                _highlightLine.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            _onCompleted?.Invoke(_getCurrentWidth());
            e.Handled = true;
        }
    }

    /// <summary>Keyboard alternative for the drag gesture (WCAG 2.5.7, audit PT1-09/PT5-07):
    /// makes the splitter focusable and resizes with Left/Right (8px steps), Enter/Space to
    /// finish. Called once per splitter from the owning page's setup.</summary>
    public void EnableKeyboardResizing(UIElement splitter, string accessibleName, double stepPx = 8)
    {
        splitter.IsTabStop = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(splitter, accessibleName);
        splitter.KeyDown += (_, e) =>
        {
            var delta = e.Key switch
            {
                Windows.System.VirtualKey.Left => -stepPx,
                Windows.System.VirtualKey.Right => stepPx,
                _ => 0,
            };
            if (delta == 0) return;
            var effective = _invertDelta ? -delta : delta;
            _setWidth(Math.Clamp(_getCurrentWidth() + effective, _minWidth, _maxWidth));
            _onCompleted?.Invoke(_getCurrentWidth());
            e.Handled = true;
        };
    }
}
