namespace DawnPlayer.Core.Audio;

/// <summary>DSP-plugin integration for the playback controller (kept apart from the core
/// playback logic so the plugin feature stays optional).</summary>
public sealed partial class PlaybackController
{
    private global::DawnPlayer.Core.Audio.Dsp.Plugins.PluginDspEffect? _pluginDspEffect;
    private global::DawnPlayer.Core.Audio.Dsp.Plugins.DspPluginLoader? _pluginDspHost;

    /// <summary>Registers the DSP plugin host and applies the persisted enable state.</summary>
    public void AttachDspPlugins(global::DawnPlayer.Core.Audio.Dsp.Plugins.DspPluginLoader host)
    {
        _pluginDspHost = host ?? throw new ArgumentNullException(nameof(host));
        ApplyPluginDsp();
    }

    /// <summary>Live-toggles plugin DSP (persisted enable state flows from settings).</summary>
    public void ApplyPluginDsp()
    {
        var effect = Volatile.Read(ref _pluginDspEffect);
        if (effect != null) effect.IsEnabled = _settings.Plugins.DspEnabled;
        Sequencer?.SetPluginDsp(_settings.Plugins.DspEnabled);
    }
}
