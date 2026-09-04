using System.Reflection;
using DawnPlayer.Core.Audio.Dsp.Plugins;

namespace DawnPlayer.Tests.Services;

/// <summary>
/// DSP plugin SDK: the loader discovers attributed IDspPlugin types from an assembly, rejects
/// duplicates, and the chain effect runs plugin instances with live enable/disable semantics.
/// Uses the sample plugin project as the fixture (same pattern as the lyrics host tests).
/// </summary>
public sealed class DspPluginHostTests
{
    private static Assembly SampleAssembly =>
        typeof(SampleDspPlugin.MonoDspPlugin).Assembly;

    [Fact]
    public void Loader_DiscoversAttributedPlugins()
    {
        var host = new DspPluginLoader();
        int found = host.LoadFromAssembly(SampleAssembly);

        Assert.Equal(1, found);
        var plugin = Assert.Single(host.Plugins);
        Assert.Equal("mono-sample", plugin.Info.Id);
        Assert.False(plugin.Info.IsExternal);
        Assert.Equal("Mono Downmix (Sample DSP)", plugin.Info.Name);
    }

    [Fact]
    public void Loader_SecondLoad_DoesNotDuplicate()
    {
        var host = new DspPluginLoader();
        host.LoadFromAssembly(SampleAssembly);
        host.LoadFromAssembly(SampleAssembly);

        Assert.Single(host.Plugins);
        Assert.NotEmpty(host.LoadErrors); // duplicate id reported
    }

    [Fact]
    public void Effect_RunsPluginProcessing_AndHonorsEnableToggle()
    {
        var host = new DspPluginLoader();
        host.LoadFromAssembly(SampleAssembly);

        var effect = new PluginDspEffect(host)
        {
            IsEnabled = false,
        };
        effect.Initialize(44100, 2);

        // Disabled: passthrough.
        var buffer = new float[] { -1f, 1f, -1f, 1f };
        effect.Process(buffer, 0, buffer.Length);
        Assert.Equal(-1f, buffer[0]);
        Assert.Equal(1f, buffer[1]);

        // Enabled: the mono plugin averages the channels.
        effect.IsEnabled = true;
        effect.Process(buffer, 0, buffer.Length);
        Assert.Equal(0f, buffer[0], 4);
        Assert.Equal(0f, buffer[1], 4);
    }

    [Fact]
    public void Effect_WithoutHost_IsSafeNoOp()
    {
        var effect = new PluginDspEffect(() => null)
        {
            IsEnabled = true,
        };
        effect.Initialize(44100, 2);

        var buffer = new float[] { 0.5f, -0.5f };
        effect.Process(buffer, 0, buffer.Length);
        Assert.Equal(0.5f, buffer[0]); // untouched
    }
}
