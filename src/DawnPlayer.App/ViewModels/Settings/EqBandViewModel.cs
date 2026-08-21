using System;
using DawnPlayer.App.Calculators;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// Presentation ViewModel representing an individual parametric equalizer filter band.
/// Handles property validation, epsilon comparisons, clamping, and change propagation.
/// </summary>
public sealed class EqBandViewModel : ViewModelBase
{
    private readonly EqBandSettings _model;
    private readonly Action? _onChanged;
    private int _index;

    public EqBandSettings Model => _model;

    public EqBandViewModel(EqBandSettings model, int index, Action? onChanged = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _index = index;
        _onChanged = onChanged;
    }

    public int Index
    {
        get => _index;
        set
        {
            if (SetProperty(ref _index, value))
            {
                OnPropertyChanged(nameof(DisplayNumber));
                OnPropertyChanged(nameof(ColorHex));
            }
        }
    }

    public string DisplayNumber => $"밴드 {_index + 1}";

    public string ColorHex => EqVisualizerCalculator.GetBandColorHex(_index);

    public EqFilterType Type
    {
        get => _model.Type;
        set
        {
            if (_model.Type != value)
            {
                _model.Type = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TypeIndex));
                OnPropertyChanged(nameof(IsGainEnabled));
                _onChanged?.Invoke();
            }
        }
    }

    public int TypeIndex
    {
        get => (int)_model.Type;
        set
        {
            if (value >= 0 && value <= 4 && (int)_model.Type != value)
            {
                Type = (EqFilterType)value;
            }
        }
    }

    public bool IsGainEnabled => _model.Type != EqFilterType.LowPass && _model.Type != EqFilterType.HighPass;

    public double FrequencyHz
    {
        get => _model.FrequencyHz;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 0), 20.0, 20000.0);
            if (Math.Abs(_model.FrequencyHz - clamped) > 0.01)
            {
                _model.FrequencyHz = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedFrequency));
                _onChanged?.Invoke();
            }
        }
    }

    public string FormattedFrequency => FormatFrequency(FrequencyHz);

    public double GainDb
    {
        get => _model.GainDb;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 1), -15.0, 15.0);
            if (Math.Abs(_model.GainDb - clamped) > 0.01)
            {
                _model.GainDb = clamped;
                OnPropertyChanged();
                _onChanged?.Invoke();
            }
        }
    }

    public double Q
    {
        get => _model.Q;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 2), 0.1, 8.0);
            if (Math.Abs(_model.Q - clamped) > 0.005)
            {
                _model.Q = clamped;
                OnPropertyChanged();
                _onChanged?.Invoke();
            }
        }
    }

    public static string FormatFrequency(double hz)
    {
        if (hz >= 1000.0)
        {
            return $"{hz / 1000.0:0.##} kHz";
        }
        return $"{hz:0} Hz";
    }
}
