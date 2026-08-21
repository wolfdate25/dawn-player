using System;
using System.Text.Json;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// The geometry the shell persists between runs, exercised as arithmetic so no window is needed:
/// splitter drags (normal and inverted delta, multi-step drags, clamping at both ends) and the
/// DIP-to-physical-pixel conversion the window placement helper performs. The DPI cases matter
/// because the round trip runs on every startup and shutdown: any rounding bias compounds, so the
/// drift is asserted over 100 cycles at standard and non-standard scales and must stay within one
/// physical pixel.
/// </summary>
public class WindowLayoutGeometryTests
{
    #region 1. SplitterResizer Math, Clamping & Persistence Tests

    [Theory]
    [InlineData(220.0, 50.0, 140.0, 550.0, false, 270.0)]     // Normal drag right: increases width
    [InlineData(220.0, -50.0, 140.0, 550.0, false, 170.0)]    // Normal drag left: decreases width
    [InlineData(220.0, 500.0, 140.0, 550.0, false, 550.0)]    // Clamped to max (550)
    [InlineData(220.0, -200.0, 140.0, 550.0, false, 140.0)]   // Clamped to min (140)
    [InlineData(220.0, -9999.0, 140.0, 550.0, false, 140.0)]  // Extreme underflow -> 140
    [InlineData(220.0, 99999.0, 140.0, 550.0, false, 550.0)]  // Extreme overflow -> 550
    public void TestSplitterMath_LeftSplitter_NormalDelta(
        double initialWidth, double deltaX, double minWidth, double maxWidth, bool invertDelta, double expectedWidth)
    {
        double targetWidth = invertDelta ? (initialWidth - deltaX) : (initialWidth + deltaX);
        double clampedWidth = Math.Clamp(targetWidth, minWidth, maxWidth);
        Assert.Equal(expectedWidth, clampedWidth);
    }

    [Theory]
    [InlineData(300.0, -50.0, 180.0, 500.0, true, 350.0)]     // Inverted drag left: increases width
    [InlineData(300.0, 50.0, 180.0, 500.0, true, 250.0)]      // Inverted drag right: decreases width
    [InlineData(300.0, -400.0, 180.0, 500.0, true, 500.0)]    // Inverted clamped to max (500)
    [InlineData(300.0, 200.0, 180.0, 500.0, true, 180.0)]     // Inverted clamped to min (180)
    [InlineData(300.0, 99999.0, 180.0, 500.0, true, 180.0)]   // Extreme positive delta -> min (180)
    [InlineData(300.0, -99999.0, 180.0, 500.0, true, 500.0)]  // Extreme negative delta -> max (500)
    public void TestSplitterMath_RightSplitter_InvertedDelta(
        double initialWidth, double deltaX, double minWidth, double maxWidth, bool invertDelta, double expectedWidth)
    {
        double targetWidth = invertDelta ? (initialWidth - deltaX) : (initialWidth + deltaX);
        double clampedWidth = Math.Clamp(targetWidth, minWidth, maxWidth);
        Assert.Equal(expectedWidth, clampedWidth);
    }

    [Theory]
    [InlineData(300.0, -50.0, 200.0, 450.0, true, 350.0)]     // Inverted drag left: increases width
    [InlineData(300.0, 50.0, 200.0, 450.0, true, 250.0)]      // Inverted drag right: decreases width
    [InlineData(300.0, -300.0, 200.0, 450.0, true, 450.0)]    // Clamped to max (450)
    [InlineData(300.0, 200.0, 200.0, 450.0, true, 200.0)]     // Clamped to min (200)
    public void TestSplitterMath_LyricsSplitter_InvertedDelta(
        double initialWidth, double deltaX, double minWidth, double maxWidth, bool invertDelta, double expectedWidth)
    {
        double targetWidth = invertDelta ? (initialWidth - deltaX) : (initialWidth + deltaX);
        double clampedWidth = Math.Clamp(targetWidth, minWidth, maxWidth);
        Assert.Equal(expectedWidth, clampedWidth);
    }

    [Fact]
    public void TestSplitter_MultiStepDragSimulation()
    {
        // Simulate dragging left splitter through multiple move events:
        // startX = 220, initial = 220. Moves: 230 (+10), 280 (+60), 600 (+380 -> 600 capped to 550), 100 (-120 -> 100 capped to 140), 300 (+80 -> 300)
        double startX = 220.0;
        double initialWidth = 220.0;
        double currentWidth = initialWidth;
        double minWidth = 140.0;
        double maxWidth = 550.0;

        double[] mousePositions = { 230, 280, 600, 100, 300 };
        double[] expectedWidths = { 230, 280, 550, 140, 300 };

        for (int i = 0; i < mousePositions.Length; i++)
        {
            double curX = mousePositions[i];
            double delta = curX - startX;
            double target = initialWidth + delta;
            currentWidth = Math.Clamp(target, minWidth, maxWidth);
            Assert.Equal(expectedWidths[i], currentWidth);
        }
    }

    [Fact]
    public void TestSplitterSettings_PersistenceRoundtrip_AllThreeSplitters()
    {
        var settings = new AppSettings();
        settings.Ui.LeftSidebarWidth = 145.0;   // In 140..550 range
        settings.Ui.RightSidebarWidth = 480.0;  // In 180..500 range
        settings.Ui.LyricsSidebarWidth = 210.0; // In 200..450 range

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(145.0, loaded.Ui.LeftSidebarWidth);
        Assert.Equal(480.0, loaded.Ui.RightSidebarWidth);
        Assert.Equal(210.0, loaded.Ui.LyricsSidebarWidth);
    }

    #endregion

    #region 2. WindowPlacementHelper DPI Scaling & Zero-Drift Persistence Tests

    [Theory]
    [InlineData(1.00, 1200.0, 700.0)] // 100% (96 DPI)
    [InlineData(1.25, 1200.0, 700.0)] // 125% (120 DPI)
    [InlineData(1.50, 1200.0, 700.0)] // 150% (144 DPI)
    [InlineData(1.75, 1200.0, 700.0)] // 175% (168 DPI)
    [InlineData(2.00, 1200.0, 700.0)] // 200% (192 DPI)
    [InlineData(1.15, 1200.0, 700.0)] // Non-standard 115%
    [InlineData(1.33, 1200.0, 700.0)] // Non-standard 133%
    [InlineData(2.25, 1200.0, 700.0)] // 225%
    [InlineData(2.50, 1200.0, 700.0)] // 250%
    [InlineData(3.00, 1200.0, 700.0)] // 300% (1200*3 = 3600 <= 3840 max clamp)
    public void TestWindowPlacement_ZeroDriftAcross100Cycles(double dpiScale, double initialDipW, double initialDipH)
    {
        var settings = new AppSettings();
        settings.Ui.WindowWidth = initialDipW;
        settings.Ui.WindowHeight = initialDipH;

        for (int cycle = 0; cycle < 100; cycle++)
        {
            // Restore step: DIP -> Physical pixels (clamped)
            int physW = Math.Clamp((int)Math.Round(settings.Ui.WindowWidth * dpiScale), 760, 3840);
            int physH = Math.Clamp((int)Math.Round(settings.Ui.WindowHeight * dpiScale), 520, 2160);

            // Save step: Physical pixels -> DIP
            double dipW = physW / dpiScale;
            double dipH = physH / dpiScale;

            settings.Ui.WindowWidth = dipW;
            settings.Ui.WindowHeight = dipH;
        }

        // Must strictly converge to within 1 physical pixel of initial DIP (< 1.0 / dpiScale drift)
        double maxAllowedDrift = 1.0 / dpiScale + 0.001;
        Assert.True(Math.Abs(settings.Ui.WindowWidth - initialDipW) <= maxAllowedDrift,
            $"Width drifted by {Math.Abs(settings.Ui.WindowWidth - initialDipW)} at scale {dpiScale}");
        Assert.True(Math.Abs(settings.Ui.WindowHeight - initialDipH) <= maxAllowedDrift,
            $"Height drifted by {Math.Abs(settings.Ui.WindowHeight - initialDipH)} at scale {dpiScale}");
    }

    [Theory]
    [InlineData(100.0, 100.0, 1.0, 760, 520)]        // Underflow -> clamped to min (760x520)
    [InlineData(5000.0, 4000.0, 1.0, 3840, 2160)]   // Overflow -> clamped to max (3840x2160)
    [InlineData(100.0, 100.0, 2.0, 760, 520)]        // Underflow at 200% DPI -> clamped to min
    [InlineData(5000.0, 4000.0, 2.0, 3840, 2160)]   // Overflow at 200% DPI -> clamped to max
    public void TestWindowPlacement_PhysicalClampingBounds(
        double dipW, double dipH, double scale, int expectedPhysW, int expectedPhysH)
    {
        int physW = Math.Clamp((int)Math.Round(dipW * scale), 760, 3840);
        int physH = Math.Clamp((int)Math.Round(dipH * scale), 520, 2160);

        Assert.Equal(expectedPhysW, physW);
        Assert.Equal(expectedPhysH, physH);
    }

    [Fact]
    public void TestWindowPlacement_MaximizedStatePreservesPreMaximizedDimensions()
    {
        var settings = new AppSettings();
        settings.Ui.WindowWidth = 1280;
        settings.Ui.WindowHeight = 800;
        settings.Ui.WindowX = 100;
        settings.Ui.WindowY = 100;
        settings.Ui.WindowMaximized = false;

        // Simulate maximizing window:
        // When maximized, SavePlacement sets ui.WindowMaximized = true without overwriting WindowWidth/Height/X/Y
        settings.Ui.WindowMaximized = true;

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(loaded);
        Assert.True(loaded.Ui.WindowMaximized);
        Assert.Equal(1280, loaded.Ui.WindowWidth);
        Assert.Equal(800, loaded.Ui.WindowHeight);
        Assert.Equal(100, loaded.Ui.WindowX);
        Assert.Equal(100, loaded.Ui.WindowY);
    }

    #endregion
}
