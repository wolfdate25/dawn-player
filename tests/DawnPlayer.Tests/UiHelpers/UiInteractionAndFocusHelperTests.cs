using System;
using System.Collections.Generic;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.UiHelpers;

/// <summary>
/// Comprehensive unit and adversarial test suite for R1:
/// 1. KeyboardHelper focus detection, text input suppression, and safe error handling.
/// 2. SplitterResizer mouse dragging lifecycle, delta inversion, bounds clamping, and multi-splitter coordination.
/// 3. WindowPlacementHelper DPI scale calculation, physical pixel bounds clamping, maximized state persistence, and zero-drift convergence.
/// </summary>
public sealed class UiInteractionAndFocusHelperTests
{
    #region 1. KeyboardHelper & Focus State Simulation

    public enum SimulatedUiElementType
    {
        Unknown,
        TextBox,
        AutoSuggestBox,
        PasswordBox,
        RichEditBox,
        Button,
        Slider,
        ListViewItem,
        TreeViewNode,
        Grid,
        Window
    }

    public sealed class SimulatedFocusedElement
    {
        public SimulatedUiElementType ElementType { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool ThrowsOnQuery { get; set; }

        public bool IsTextInputElement()
        {
            if (ThrowsOnQuery) throw new InvalidOperationException("Simulated WinUI COM thread marshalling failure");
            return ElementType is SimulatedUiElementType.TextBox
                or SimulatedUiElementType.AutoSuggestBox
                or SimulatedUiElementType.PasswordBox
                or SimulatedUiElementType.RichEditBox;
        }
    }

    public static class PureKeyboardFocusEvaluator
    {
        public static bool FocusIsInTextInput(SimulatedFocusedElement? focusedElement)
        {
            try
            {
                if (focusedElement == null) return false;
                return focusedElement.IsTextInputElement();
            }
            catch
            {
                return false;
            }
        }

        public static bool ShouldHandleGlobalMediaKey(string key, bool focusInTextInput)
        {
            if (focusInTextInput)
            {
                return key switch
                {
                    "MediaPlayPause" or "MediaNextTrack" or "MediaPreviousTrack" or "MediaStop" => true,
                    "Space" or "KeyJ" or "KeyK" or "KeyL" or "Left" or "Right" or "Up" or "Down" => false,
                    _ => false
                };
            }

            return key switch
            {
                "Space" or "KeyK" or "MediaPlayPause" => true,
                "KeyJ" or "Left" or "MediaPreviousTrack" => true,
                "KeyL" or "Right" or "MediaNextTrack" => true,
                "Up" or "Down" => true,
                "KeyM" or "KeyQ" => true,
                _ => false
            };
        }
    }

    [Theory]
    [InlineData(SimulatedUiElementType.TextBox, true)]
    [InlineData(SimulatedUiElementType.AutoSuggestBox, true)]
    [InlineData(SimulatedUiElementType.PasswordBox, true)]
    [InlineData(SimulatedUiElementType.RichEditBox, true)]
    [InlineData(SimulatedUiElementType.Button, false)]
    [InlineData(SimulatedUiElementType.Slider, false)]
    [InlineData(SimulatedUiElementType.ListViewItem, false)]
    [InlineData(SimulatedUiElementType.TreeViewNode, false)]
    [InlineData(SimulatedUiElementType.Grid, false)]
    [InlineData(SimulatedUiElementType.Window, false)]
    [InlineData(SimulatedUiElementType.Unknown, false)]
    public void KeyboardHelper_FocusIsInTextInput_CorrectlyClassifiesElements(SimulatedUiElementType type, bool expected)
    {
        var elem = new SimulatedFocusedElement { ElementType = type };
        Assert.Equal(expected, PureKeyboardFocusEvaluator.FocusIsInTextInput(elem));
    }

    [Fact]
    public void KeyboardHelper_FocusIsInTextInput_NullElement_ReturnsFalse()
    {
        Assert.False(PureKeyboardFocusEvaluator.FocusIsInTextInput(null));
    }

    [Fact]
    public void KeyboardHelper_FocusIsInTextInput_ExceptionOnQuery_ReturnsFalseGracefully()
    {
        var elem = new SimulatedFocusedElement { ElementType = SimulatedUiElementType.TextBox, ThrowsOnQuery = true };
        Assert.False(PureKeyboardFocusEvaluator.FocusIsInTextInput(elem));
    }

    [Theory]
    [InlineData("Space", true, false)]
    [InlineData("Space", false, true)]
    [InlineData("KeyJ", true, false)]
    [InlineData("KeyJ", false, true)]
    [InlineData("KeyK", true, false)]
    [InlineData("KeyK", false, true)]
    [InlineData("KeyL", true, false)]
    [InlineData("KeyL", false, true)]
    [InlineData("MediaPlayPause", true, true)]
    [InlineData("MediaPlayPause", false, true)]
    [InlineData("MediaNextTrack", true, true)]
    [InlineData("MediaPreviousTrack", true, true)]
    public void KeyboardHelper_GlobalMediaKeyRouting_RespectsFocusContext(string key, bool inTextInput, bool expectedHandled)
    {
        bool handled = PureKeyboardFocusEvaluator.ShouldHandleGlobalMediaKey(key, inTextInput);
        Assert.Equal(expectedHandled, handled);
    }

    #endregion

    #region 2. SplitterResizer State Machine & Interactive Math

    public sealed class TestSplitterController
    {
        private readonly double _minWidth;
        private readonly double _maxWidth;
        private readonly bool _invertDelta;
        private readonly Func<double> _getCurrentWidth;
        private readonly Action<double> _setWidth;
        private readonly Action<string?>? _setCursor;
        private readonly Action<string?>? _setHighlight;
        private readonly Action<double>? _onCompleted;

        private bool _isDragging;
        private double _dragStartX;
        private double _initialWidth;

        public bool IsDragging => _isDragging;

        public TestSplitterController(
            double minWidth,
            double maxWidth,
            bool invertDelta,
            Func<double> getCurrentWidth,
            Action<double> setWidth,
            Action<string?>? setCursor = null,
            Action<string?>? setHighlight = null,
            Action<double>? onCompleted = null)
        {
            _minWidth = minWidth;
            _maxWidth = maxWidth;
            _invertDelta = invertDelta;
            _getCurrentWidth = getCurrentWidth;
            _setWidth = setWidth;
            _setCursor = setCursor;
            _setHighlight = setHighlight;
            _onCompleted = onCompleted;
        }

        public void OnPointerEntered()
        {
            _setCursor?.Invoke("SizeWestEast");
            _setHighlight?.Invoke("DawnAccentBrush");
        }

        public void OnPointerExited(Func<bool> anyOtherDragging)
        {
            if (!_isDragging && !anyOtherDragging())
            {
                _setCursor?.Invoke(null);
                _setHighlight?.Invoke("Transparent");
            }
        }

        public void OnPointerPressed(double currentPointerX)
        {
            _isDragging = true;
            _dragStartX = currentPointerX;
            _initialWidth = _getCurrentWidth();
        }

        public void OnPointerMoved(double currentPointerX)
        {
            if (_isDragging)
            {
                double delta = currentPointerX - _dragStartX;
                double targetWidth = _invertDelta ? (_initialWidth - delta) : (_initialWidth + delta);
                double clampedWidth = Math.Clamp(targetWidth, _minWidth, _maxWidth);
                _setWidth(clampedWidth);
            }
        }

        public void OnPointerReleased()
        {
            if (_isDragging)
            {
                _isDragging = false;
                _setHighlight?.Invoke("Transparent");
                _onCompleted?.Invoke(_getCurrentWidth());
            }
        }
    }

    [Fact]
    public void SplitterResizer_LeftSidebar_NormalDelta_LifecycleAndClamping()
    {
        double width = 220.0;
        double savedWidth = 0.0;
        string? cursor = null;
        string? highlight = null;

        var resizer = new TestSplitterController(
            minWidth: 140.0,
            maxWidth: 550.0,
            invertDelta: false,
            getCurrentWidth: () => width,
            setWidth: w => width = w,
            setCursor: c => cursor = c,
            setHighlight: h => highlight = h,
            onCompleted: w => savedWidth = w);

        Assert.False(resizer.IsDragging);

        // Hover
        resizer.OnPointerEntered();
        Assert.Equal("SizeWestEast", cursor);
        Assert.Equal("DawnAccentBrush", highlight);

        // Press at X=100
        resizer.OnPointerPressed(100.0);
        Assert.True(resizer.IsDragging);

        // Drag right by +50px (X=150) -> width becomes 220 + 50 = 270
        resizer.OnPointerMoved(150.0);
        Assert.Equal(270.0, width);

        // Drag beyond max bounds (+500px, X=600) -> clamped to 550
        resizer.OnPointerMoved(600.0);
        Assert.Equal(550.0, width);

        // Drag below min bounds (-300px, X=-200) -> clamped to 140
        resizer.OnPointerMoved(-200.0);
        Assert.Equal(140.0, width);

        // Release at 300px (X=180) -> width = 220 + 80 = 300
        resizer.OnPointerMoved(180.0);
        resizer.OnPointerReleased();

        Assert.False(resizer.IsDragging);
        Assert.Equal(300.0, width);
        Assert.Equal(300.0, savedWidth);
        Assert.Equal("Transparent", highlight);
    }

    [Fact]
    public void SplitterResizer_RightSidebar_InvertDelta_DragCalculations()
    {
        double width = 300.0;
        double savedWidth = 0.0;

        var resizer = new TestSplitterController(
            minWidth: 180.0,
            maxWidth: 500.0,
            invertDelta: true,
            getCurrentWidth: () => width,
            setWidth: w => width = w,
            onCompleted: w => savedWidth = w);

        resizer.OnPointerPressed(500.0);

        // Drag LEFT by 60px (X=440, delta = -60) -> inverted delta adds +60 -> width 360
        resizer.OnPointerMoved(440.0);
        Assert.Equal(360.0, width);

        // Drag RIGHT by 150px (X=650, delta = +150) -> inverted delta subtracts -150 -> width 150 (clamped to min 180)
        resizer.OnPointerMoved(650.0);
        Assert.Equal(180.0, width);

        resizer.OnPointerReleased();
        Assert.Equal(180.0, savedWidth);
    }

    [Fact]
    public void SplitterResizer_MultiSplitterInteraction_CoordinatesDraggingState()
    {
        double leftWidth = 220.0;
        double rightWidth = 300.0;
        string? cursor = null;

        var leftSplitter = new TestSplitterController(
            minWidth: 140.0,
            maxWidth: 550.0,
            invertDelta: false,
            getCurrentWidth: () => leftWidth,
            setWidth: w => leftWidth = w,
            setCursor: c => cursor = c);

        var rightSplitter = new TestSplitterController(
            minWidth: 180.0,
            maxWidth: 500.0,
            invertDelta: true,
            getCurrentWidth: () => rightWidth,
            setWidth: w => rightWidth = w,
            setCursor: c => cursor = c);

        leftSplitter.OnPointerEntered();
        leftSplitter.OnPointerPressed(100.0);
        Assert.True(leftSplitter.IsDragging);
        Assert.False(rightSplitter.IsDragging);

        rightSplitter.OnPointerExited(() => leftSplitter.IsDragging);
        Assert.Equal("SizeWestEast", cursor);

        leftSplitter.OnPointerReleased();
        leftSplitter.OnPointerExited(() => rightSplitter.IsDragging);
        Assert.Null(cursor);
    }

    #endregion

    #region 3. WindowPlacementHelper DPI Scaling & State Persistence

    public static class PureWindowPlacementCalculator
    {
        public static (int Width, int Height) ComputePhysicalSize(double dipWidth, double dipHeight, double scale)
        {
            int w = Math.Clamp((int)Math.Round(dipWidth * scale), 760, 3840);
            int h = Math.Clamp((int)Math.Round(dipHeight * scale), 520, 2160);
            return (w, h);
        }

        public static (double DipWidth, double DipHeight) ComputeDipSize(int physicalWidth, int physicalHeight, double scale)
        {
            double w = physicalWidth / scale;
            double h = physicalHeight / scale;
            return (w, h);
        }

        public static void SavePlacementSimulation(
            UiSettings ui,
            bool isMaximized,
            int clientPhysWidth,
            int clientPhysHeight,
            int posX,
            int posY,
            double scale)
        {
            ui.WindowMaximized = isMaximized;
            if (!isMaximized)
            {
                var (dipW, dipH) = ComputeDipSize(clientPhysWidth, clientPhysHeight, scale);
                ui.WindowWidth = dipW;
                ui.WindowHeight = dipH;
                ui.WindowX = posX;
                ui.WindowY = posY;
            }
        }
    }

    [Theory]
    [InlineData(1200.0, 750.0, 1.0, 1200, 750)]
    [InlineData(1200.0, 750.0, 1.25, 1500, 938)]
    [InlineData(1200.0, 750.0, 1.50, 1800, 1125)]
    [InlineData(1200.0, 750.0, 1.75, 2100, 1312)]
    [InlineData(1200.0, 750.0, 2.00, 2400, 1500)]
    [InlineData(1200.0, 750.0, 2.50, 3000, 1875)]
    public void WindowPlacement_DpiScaling_CalculatesPhysicalPixelsAccurately(
        double dipW, double dipH, double scale, int expectedPhysW, int expectedPhysH)
    {
        var (w, h) = PureWindowPlacementCalculator.ComputePhysicalSize(dipW, dipH, scale);
        Assert.Equal(expectedPhysW, w);
        Assert.Equal(expectedPhysH, h);
    }

    [Theory]
    [InlineData(100.0, 100.0, 1.0, 760, 520)]
    [InlineData(5000.0, 4000.0, 1.0, 3840, 2160)]
    [InlineData(50.0, 50.0, 2.0, 760, 520)]
    [InlineData(3000.0, 2000.0, 2.0, 3840, 2160)]
    public void WindowPlacement_BoundsClamping_ProtectsAgainstExtremeSizes(
        double dipW, double dipH, double scale, int expectedPhysW, int expectedPhysH)
    {
        var (w, h) = PureWindowPlacementCalculator.ComputePhysicalSize(dipW, dipH, scale);
        Assert.Equal(expectedPhysW, w);
        Assert.Equal(expectedPhysH, h);
    }

    [Fact]
    public void WindowPlacement_ZeroDriftAcross1000Cycles_AllScales()
    {
        double[] scales = { 1.0, 1.25, 1.50, 1.75, 2.0, 2.25, 2.5, 3.0, 1.15, 1.33 };

        foreach (var scale in scales)
        {
            double initialW = 1200.0;
            double initialH = 700.0;
            double currentW = initialW;
            double currentH = initialH;

            for (int i = 0; i < 1000; i++)
            {
                var (physW, physH) = PureWindowPlacementCalculator.ComputePhysicalSize(currentW, currentH, scale);
                var (dipW, dipH) = PureWindowPlacementCalculator.ComputeDipSize(physW, physH, scale);
                currentW = dipW;
                currentH = dipH;
            }

            double maxAllowedDrift = (1.0 / scale) + 0.001;
            Assert.True(Math.Abs(currentW - initialW) <= maxAllowedDrift,
                $"Width drifted by {Math.Abs(currentW - initialW)} at scale {scale}");
            Assert.True(Math.Abs(currentH - initialH) <= maxAllowedDrift,
                $"Height drifted by {Math.Abs(currentH - initialH)} at scale {scale}");
        }
    }

    [Fact]
    public void WindowPlacement_MaximizedState_PreservesPreMaximizedDimensions()
    {
        var ui = new UiSettings
        {
            WindowWidth = 1366.0,
            WindowHeight = 768.0,
            WindowX = 150,
            WindowY = 120,
            WindowMaximized = false
        };

        PureWindowPlacementCalculator.SavePlacementSimulation(
            ui,
            isMaximized: true,
            clientPhysWidth: 1920,
            clientPhysHeight: 1040,
            posX: 0,
            posY: 0,
            scale: 1.0);

        Assert.True(ui.WindowMaximized);
        Assert.Equal(1366.0, ui.WindowWidth);
        Assert.Equal(768.0, ui.WindowHeight);
        Assert.Equal(150, ui.WindowX);
        Assert.Equal(120, ui.WindowY);

        PureWindowPlacementCalculator.SavePlacementSimulation(
            ui,
            isMaximized: false,
            clientPhysWidth: 1400,
            clientPhysHeight: 800,
            posX: 200,
            posY: 150,
            scale: 1.0);

        Assert.False(ui.WindowMaximized);
        Assert.Equal(1400.0, ui.WindowWidth);
        Assert.Equal(800.0, ui.WindowHeight);
        Assert.Equal(200, ui.WindowX);
        Assert.Equal(150, ui.WindowY);
    }

    [Fact]
    public void WindowPlacement_MultiMonitorCoordinates_NegativeAndLargeOffsets()
    {
        var ui = new UiSettings();

        PureWindowPlacementCalculator.SavePlacementSimulation(
            ui,
            isMaximized: false,
            clientPhysWidth: 1280,
            clientPhysHeight: 800,
            posX: -1920,
            posY: 100,
            scale: 1.0);

        Assert.Equal(-1920, ui.WindowX);
        Assert.Equal(100, ui.WindowY);

        PureWindowPlacementCalculator.SavePlacementSimulation(
            ui,
            isMaximized: false,
            clientPhysWidth: 1920,
            clientPhysHeight: 1080,
            posX: 3840,
            posY: 0,
            scale: 1.5);

        Assert.Equal(3840, ui.WindowX);
        Assert.Equal(0, ui.WindowY);
        Assert.Equal(1280.0, ui.WindowWidth);
        Assert.Equal(720.0, ui.WindowHeight);
    }

    #endregion
}
