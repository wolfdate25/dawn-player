using System;
using DawnPlayer.Core.Audio.Dsp;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// SoftLimiterDspEffect transfer-curve proofs: monotonicity, C1 smoothness at the threshold,
/// saturation bounds, odd symmetry, bit-exact pass-through, and buffer-slice isolation.
/// </summary>
public class SoftLimiterContinuityTests
{
    [Fact]
    public void SoftLimiter_Monotonicity_DenseSweepAcrossFullDynamicRange()
    {
        // Test dense sweep from -10.0 to +10.0
        // In float32 arithmetic, for |x| > 3.0 the derivative becomes smaller than float32 ULP,
        // so we assert non-decreasing monotonicity (y >= prevY) across the entire range [-10, 10],
        // and strictly increasing monotonicity (y > prevY) across [-3.0, 3.0] with step 0.001f.
        float threshold = 0.90f;
        float prevY = float.NegativeInfinity;

        for (float x = -10.0f; x <= 10.0f; x += 0.001f)
        {
            float y = SoftLimiterDspEffect.Limit(x, threshold);
            Assert.True(y >= prevY, $"Non-decreasing monotonicity violated at x = {x}: y({y}) < prevY({prevY})");
            prevY = y;
        }

        prevY = float.NegativeInfinity;
        for (float x = -3.0f; x <= 3.0f; x += 0.001f)
        {
            float y = SoftLimiterDspEffect.Limit(x, threshold);
            Assert.True(y > prevY, $"Strict monotonicity violated at x = {x}: y({y}) <= prevY({prevY})");
            prevY = y;
        }
    }

    [Fact]
    public void SoftLimiter_C1Smoothness_ContinuousFirstDerivativeAtThreshold()
    {
        // Mathematical proof check:
        // y(x) = x for |x| <= T
        // y(x) = T + (1-T)*(x-T) / (1-2T+x) for x > T
        // Left derivative at T: d/dx(x) = 1.0
        // Right derivative at T: d/dx[T + (1-T)*(x-T)/(1-2T+x)] = (1-T)^2 / (1-2T+x)^2 = (1-T)^2 / (1-T)^2 = 1.0
        // Numerical derivative test using central difference: [f(T + h) - f(T - h)] / (2h)
        float threshold = 0.85f;
        float h = 1e-4f;

        float fPlus = SoftLimiterDspEffect.Limit(threshold + h, threshold);
        float fMinus = SoftLimiterDspEffect.Limit(threshold - h, threshold);
        float numericalDeriv = (fPlus - fMinus) / (2.0f * h);

        Assert.Equal(1.0f, numericalDeriv, precision: 3);

        // Same for negative threshold
        float fNegPlus = SoftLimiterDspEffect.Limit(-threshold + h, threshold);
        float fNegMinus = SoftLimiterDspEffect.Limit(-threshold - h, threshold);
        float numericalDerivNeg = (fNegPlus - fNegMinus) / (2.0f * h);

        Assert.Equal(1.0f, numericalDerivNeg, precision: 3);
    }

    [Theory]
    [InlineData(0.50f)]
    [InlineData(0.75f)]
    [InlineData(0.90f)]
    [InlineData(0.95f)]
    [InlineData(0.99f)]
    public void SoftLimiter_SaturationBounds_StrictlyBoundedBetweenMinusOneAndPlusOne(float threshold)
    {
        float[] extremeInputs = {
            1.0f, 1.01f, 1.1f, 1.5f, 2.0f, 5.0f, 10.0f, 100.0f, 1000.0f, 100000.0f,
            -1.0f, -1.01f, -1.1f, -1.5f, -2.0f, -5.0f, -10.0f, -100.0f, -1000.0f, -100000.0f
        };

        foreach (var x in extremeInputs)
        {
            float y = SoftLimiterDspEffect.Limit(x, threshold);
            Assert.True(y <= 1.0f, $"Output y={y} for x={x} exceeded upper bound 1.0 (threshold={threshold})");
            Assert.True(y >= -1.0f, $"Output y={y} for x={x} went below lower bound -1.0 (threshold={threshold})");

            if (x > 0)
            {
                Assert.True(y >= threshold, $"Positive output y={y} should be >= threshold={threshold}");
                Assert.True(y <= x, $"Positive limited y={y} should be <= input x={x}");
            }
            else
            {
                Assert.True(y <= -threshold, $"Negative output y={y} should be <= -threshold={-threshold}");
                Assert.True(y >= x, $"Negative limited y={y} should be >= input x={x}");
            }
        }
    }

    [Fact]
    public void SoftLimiter_OddSymmetry_FOfMinusXEqualsMinusFOfX()
    {
        float threshold = 0.90f;
        for (float x = 0.0f; x <= 10.0f; x += 0.05f)
        {
            float yPos = SoftLimiterDspEffect.Limit(x, threshold);
            float yNeg = SoftLimiterDspEffect.Limit(-x, threshold);
            Assert.Equal(-yPos, yNeg);
        }
    }

    [Fact]
    public void SoftLimiter_ZeroDistortionBelowThreshold_BitExactPassThrough()
    {
        float threshold = 0.90f;
        for (float x = -0.90f; x <= 0.90f; x += 0.01f)
        {
            float y = SoftLimiterDspEffect.Limit(x, threshold);
            Assert.Equal(x, y);
        }
    }

    [Theory]
    [InlineData(-5.0f, 0.50f)]
    [InlineData(0.10f, 0.50f)]
    [InlineData(0.49f, 0.50f)]
    [InlineData(1.50f, 0.99f)]
    [InlineData(10.0f, 0.99f)]
    public void SoftLimiter_ThresholdClamping_EnforcesSafeRange(float inputThreshold, float expectedEffectiveThreshold)
    {
        var limiter = new SoftLimiterDspEffect(inputThreshold);
        Assert.Equal(expectedEffectiveThreshold, limiter.Threshold);
    }

    [Fact]
    public void SoftLimiter_ProcessWithBufferOffset_OnlyModifiesTargetSlice()
    {
        float[] samples = { 0.5f, 1.5f, -2.0f, 0.8f };
        var limiter = new SoftLimiterDspEffect(threshold: 0.90f);

        float[] destination = new float[10];
        // The canary itself sits far above the threshold, so any write outside the
        // requested slice would be compressed and therefore visible.
        for (int i = 0; i < destination.Length; i++) destination[i] = 999.0f;

        int offset = 3;
        Array.Copy(samples, 0, destination, offset, samples.Length);

        limiter.Process(destination, offset, samples.Length);

        // Indices 0, 1, 2 should remain canary
        Assert.Equal(999.0f, destination[0]);
        Assert.Equal(999.0f, destination[1]);
        Assert.Equal(999.0f, destination[2]);

        // Indices 3..6 should contain limited samples
        Assert.Equal(0.5f, destination[3]);
        Assert.True(destination[4] > 0.90f && destination[4] < 1.0f);
        Assert.True(destination[5] < -0.90f && destination[5] > -1.0f);
        Assert.Equal(0.8f, destination[6]);

        // Indices 7, 8, 9 should remain canary
        Assert.Equal(999.0f, destination[7]);
        Assert.Equal(999.0f, destination[8]);
        Assert.Equal(999.0f, destination[9]);
    }
}
