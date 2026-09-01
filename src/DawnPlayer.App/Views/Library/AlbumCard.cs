using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DawnPlayer.App.Views;

/// <summary>
/// Presentation model for an album card in the cover grid view with dynamic width and height scaling
/// and active selection / caret state when expanded in the Eole Showlist Drawer.
/// </summary>
public sealed class AlbumCard : INotifyPropertyChanged
{
    public string Key { get; set; } = "";
    public string Album { get; set; } = "";
    public string Artist { get; set; } = "";
    public int Year { get; set; }
    public string? ArtPath { get; set; }
    public List<Track> Tracks { get; } = new();
    private BitmapImage? _art;
    public BitmapImage? Art => _art ??= (!string.IsNullOrEmpty(ArtPath) ? CreateArt() : null);
    public int Count => Tracks.Count;

    public bool HasYear => Year > 0;
    public string YearString => Year > 0 ? Year.ToString(CultureInfo.InvariantCulture) : "";
    public string FormattedDuration => TextFormat.LongDuration(TimeSpan.FromMilliseconds(Tracks.Sum(t => t.DurationMs)));

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    private double _cardWidth = 144;
    public double CardWidth
    {
        get => _cardWidth;
        set
        {
            if (Math.Abs(_cardWidth - value) > 0.1)
            {
                _cardWidth = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ImageHeight));
            }
        }
    }

    public double ImageHeight => Math.Max(20, _cardWidth - 4);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private BitmapImage? CreateArt()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ArtPath) || !System.IO.File.Exists(ArtPath))
                return null;

            // DecodePixelWidth has to be set before UriSource: assigning the URI kicks off the
            // decode, so the previous "new BitmapImage(uri) then set DecodePixelWidth" order
            // silently decoded every cover at full resolution.
            var bmp = new BitmapImage { DecodePixelWidth = 320 };
            bmp.UriSource = new Uri(ArtPath, UriKind.Absolute);
            return bmp;
        }
        catch { return null; }
    }
}
