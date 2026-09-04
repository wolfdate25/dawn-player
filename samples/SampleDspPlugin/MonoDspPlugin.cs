using DawnPlayer.Plugins;

namespace SampleDspPlugin;

[DspPlugin("mono-sample", "Mono Downmix (Sample DSP)", "1.0.0", "Dawn Player")]
public sealed class MonoDspPlugin : IDspPlugin
{
    public string Id => "mono-sample";
    public string DisplayName => "Mono Downmix (Sample DSP)";

    public IDspEffectInstance CreateEffect() => new MonoEffect();

    private sealed class MonoEffect : IDspEffectInstance
    {
        private int _channels = 2;

        public void Initialize(int sampleRate, int channels) => _channels = Math.Max(1, channels);

        public void Process(float[] buffer, int offset, int count)
        {
            int channels = _channels;
            int frames = count / channels;
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = offset + f * channels;
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += buffer[baseIdx + c];
                float mono = sum / channels;
                for (int c = 0; c < channels; c++) buffer[baseIdx + c] = mono;
            }
        }

        public void Reset() { }
    }
}
