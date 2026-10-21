using System;
using System.IO;
using System.Linq;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Persistence;
using Xunit;

namespace DawnPlayer.Tests.Audio;

public class AutoEqParserTests
{
    private const string SampleAutoEqText = @"
Preamp: -6.6 dB
Filter 1: ON PK Fc 28 Hz Gain 4.8 dB Q 1.34
Filter 2: ON LSC Fc 105 Hz Gain 5.5 dB Q 0.71
Filter 3: ON PK Fc 202 Hz Gain 2.6 dB Q 1.51
Filter 4: ON PK Fc 1113 Hz Gain -2.1 dB Q 2.01
Filter 5: ON PK Fc 3004 Hz Gain 3.8 dB Q 1.63
Filter 6: ON PK Fc 5760 Hz Gain -3.8 dB Q 2.87
Filter 7: ON PK Fc 8000 Hz Gain 4.2 dB Q 0.80
Filter 8: ON HSC Fc 10000 Hz Gain -2.0 dB Q 0.70
Filter 9: ON LP Fc 18000 Hz Gain 0.0 dB Q 0.71
Filter 10: ON HP Fc 20 Hz Gain 0.0 dB Q 0.71
Filter 11: OFF PK Fc 1000 Hz Gain 3.0 dB Q 1.00
";

    [Fact]
    public void AutoEqParser_StandardText_ParsesCorrectly()
    {
        var profile = AutoEqParser.Parse(SampleAutoEqText, "Sennheiser HD 600");

        Assert.NotNull(profile);
        Assert.Equal("Sennheiser HD 600", profile.Name);
        Assert.Equal(-6.6, profile.PreampDb, precision: 1);
        Assert.True(profile.Enabled);

        // Filter 11 is OFF, so only 10 active bands should be parsed
        Assert.Equal(10, profile.Bands.Count);

        // Filter 1: PK 28Hz +4.8dB Q 1.34
        Assert.Equal(EqFilterType.PeakEq, profile.Bands[0].Type);
        Assert.Equal(28.0, profile.Bands[0].FrequencyHz, precision: 1);
        Assert.Equal(4.8, profile.Bands[0].GainDb, precision: 1);
        Assert.Equal(1.34, profile.Bands[0].Q, precision: 2);

        // Filter 2: LSC 105Hz +5.5dB Q 0.71
        Assert.Equal(EqFilterType.LowShelf, profile.Bands[1].Type);
        Assert.Equal(105.0, profile.Bands[1].FrequencyHz, precision: 1);
        Assert.Equal(5.5, profile.Bands[1].GainDb, precision: 1);

        // Filter 8: HSC 10000Hz -2.0dB Q 0.70
        Assert.Equal(EqFilterType.HighShelf, profile.Bands[7].Type);
        Assert.Equal(10000.0, profile.Bands[7].FrequencyHz, precision: 1);
        Assert.Equal(-2.0, profile.Bands[7].GainDb, precision: 1);

        // Filter 9: LP 18000Hz
        Assert.Equal(EqFilterType.LowPass, profile.Bands[8].Type);
        Assert.Equal(18000.0, profile.Bands[8].FrequencyHz, precision: 1);

        // Filter 10: HP 20Hz
        Assert.Equal(EqFilterType.HighPass, profile.Bands[9].Type);
        Assert.Equal(20.0, profile.Bands[9].FrequencyHz, precision: 1);
    }

    [Fact]
    public void AutoEqParser_ClampingAndSanitization_EnforcesInvariants()
    {
        string extremeText = @"
Preamp: -30.0 dB
Filter 1: ON PK Fc 5 Hz Gain 35.0 dB Q 0.01
Filter 2: ON PK Fc 40000 Hz Gain -40.0 dB Q 99.0
Filter 3: ON INVALID_TYPE Fc 1000 Hz Gain 0.0 dB Q 1.0
";
        var profile = AutoEqParser.Parse(extremeText, "Extreme Test");

        Assert.NotNull(profile);
        // Preamp clamped to [-12, 12]
        Assert.Equal(-12.0, profile.PreampDb);

        // Invalid type skipped or defaulted, valid ones clamped
        Assert.True(profile.Bands.Count >= 2);

        // Band 1: Freq clamped min 20Hz, Gain max 15dB, Q min 0.1
        Assert.Equal(20.0, profile.Bands[0].FrequencyHz);
        Assert.Equal(15.0, profile.Bands[0].GainDb);
        Assert.Equal(0.1, profile.Bands[0].Q);

        // Band 2: Freq clamped max 20000Hz, Gain min -15dB, Q max 8.0
        Assert.Equal(20000.0, profile.Bands[1].FrequencyHz);
        Assert.Equal(-15.0, profile.Bands[1].GainDb);
        Assert.Equal(8.0, profile.Bands[1].Q);
    }

    [Fact]
    public void AutoEqParser_BandLimit20_TruncatesSafely()
    {
        var lines = new System.Collections.Generic.List<string> { "Preamp: 0.0 dB" };
        for (int i = 1; i <= 30; i++)
        {
            lines.Add($"Filter {i}: ON PK Fc {100 * i} Hz Gain 1.0 dB Q 1.0");
        }
        string text = string.Join(Environment.NewLine, lines);

        var profile = AutoEqParser.Parse(text, "30 Bands Profile");

        // Engine supports maximum 20 bands
        Assert.Equal(20, profile.Bands.Count);
        Assert.Equal(100.0, profile.Bands[0].FrequencyHz);
        Assert.Equal(2000.0, profile.Bands[19].FrequencyHz);
    }

    [Fact]
    public void AutoEqParser_MalformedInput_DoesNotCrash()
    {
        Assert.NotNull(AutoEqParser.Parse(null!, "Null Test"));
        Assert.NotNull(AutoEqParser.Parse("", "Empty Test"));
        Assert.NotNull(AutoEqParser.Parse("This is not an AutoEq file at all! \n Random words: 12345", "Garbage"));
    }

    [Fact]
    public void AutoEqParser_RoundTrip_ExportMatchesParsedValues()
    {
        var original = new EqProfile
        {
            Name = "Audeze LCD-X",
            PreampDb = -5.4,
            Enabled = true,
            Bands =
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 35.0, GainDb = 4.2, Q = 1.4 },
                new EqBandSettings { Type = EqFilterType.LowShelf, FrequencyHz = 105.0, GainDb = 2.0, Q = 0.71 },
                new EqBandSettings { Type = EqFilterType.HighShelf, FrequencyHz = 8500.0, GainDb = -1.5, Q = 0.71 }
            }
        };

        string exportedText = AutoEqParser.Export(original);
        Assert.Contains("Preamp: -5.4 dB", exportedText);
        Assert.Contains("Filter 1: ON PK Fc 35 Hz Gain 4.2 dB Q 1.4", exportedText);
        Assert.Contains("Filter 2: ON LSC Fc 105 Hz Gain 2 dB Q 0.71", exportedText);
        Assert.Contains("Filter 3: ON HSC Fc 8500 Hz Gain -1.5 dB Q 0.71", exportedText);

        var reimported = AutoEqParser.Parse(exportedText, "Audeze LCD-X");
        Assert.Equal(original.PreampDb, reimported.PreampDb, precision: 1);
        Assert.Equal(original.Bands.Count, reimported.Bands.Count);
        for (int i = 0; i < original.Bands.Count; i++)
        {
            Assert.Equal(original.Bands[i].Type, reimported.Bands[i].Type);
            Assert.Equal(original.Bands[i].FrequencyHz, reimported.Bands[i].FrequencyHz, precision: 1);
            Assert.Equal(original.Bands[i].GainDb, reimported.Bands[i].GainDb, precision: 1);
            Assert.Equal(original.Bands[i].Q, reimported.Bands[i].Q, precision: 2);
        }
    }

    [Fact]
    public void AutoEqParser_JsonProfile_RoundTripSerialization()
    {
        var profile = new EqProfile
        {
            Id = "custom_lcd2",
            Name = "LCD-2 Classic",
            PreampDb = -3.5,
            Enabled = true,
            Bands =
            {
                new EqBandSettings { Type = EqFilterType.PeakEq, FrequencyHz = 1200, GainDb = -2.5, Q = 1.8 }
            }
        };

        string json = AutoEqParser.SerializeJson(profile);
        Assert.False(string.IsNullOrWhiteSpace(json));

        var deserialized = AutoEqParser.DeserializeJson(json);
        Assert.NotNull(deserialized);
        Assert.Equal(profile.Name, deserialized.Name);
        Assert.Equal(profile.PreampDb, deserialized.PreampDb, precision: 1);
        Assert.Single(deserialized.Bands);
        Assert.Equal(profile.Bands[0].FrequencyHz, deserialized.Bands[0].FrequencyHz);
        Assert.Equal(profile.Bands[0].GainDb, deserialized.Bands[0].GainDb);
    }

    [Fact]
    public void AutoEqParser_ViewModel_ImportExportIntegration()
    {
        var settings = new AppSettings();
        var eqService = new DawnPlayer.App.Services.EqSettingsService(settings);
        var audioService = new DawnPlayer.App.Services.AudioSettingsService(settings);
        var vm = new DawnPlayer.App.ViewModels.Settings.EqualizerSettingsViewModel(eqService, audioService, settings);

        // Import AutoEq text
        var imported = vm.ImportAutoEqText(SampleAutoEqText, "VM Imported Sennheiser");
        Assert.NotNull(imported);
        Assert.Equal("VM Imported Sennheiser", imported.Name);
        Assert.Equal(imported.Id, vm.SelectedProfile?.Id);
        Assert.Equal(-6.6, vm.PreampDb, precision: 1);
        Assert.Equal(10, vm.BandCount);

        // Export AutoEq
        string? exportedText = vm.ExportCurrentAsAutoEq();
        Assert.NotNull(exportedText);
        Assert.Contains("Preamp: -6.6 dB", exportedText);

        // Export JSON
        string? exportedJson = vm.ExportCurrentAsJson();
        Assert.NotNull(exportedJson);
        Assert.Contains("VM Imported Sennheiser", exportedJson);
    }
}
