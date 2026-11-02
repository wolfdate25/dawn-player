using System;
using System.Collections.Generic;
using DawnPlayer.App.Helpers;
using DawnPlayer.Core.Lyrics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;

namespace DawnPlayer.App.Controls;

/// <summary>
/// Word-level karaoke text for one lyric line. The Runs are built once per Words assignment;
/// an active-word transition swaps at most two Run brushes — never a per-tick inline rebuild
/// (plan §10.3, I6). Colors follow the app theme: sung = TextPrimary, current = DawnAccentText,
/// upcoming = TextTertiary, resolved per rebuild so a theme switch repaints on the spot.
/// </summary>
public sealed class KaraokeTextBlock : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(KaraokeTextBlock), new PropertyMetadata("", OnWordsChanged));

    public static readonly DependencyProperty WordsProperty = DependencyProperty.Register(
        nameof(Words), typeof(IReadOnlyList<LrcWord>), typeof(KaraokeTextBlock), new PropertyMetadata(null, OnWordsChanged));

    public static readonly DependencyProperty ActiveWordIndexProperty = DependencyProperty.Register(
        nameof(ActiveWordIndex), typeof(int), typeof(KaraokeTextBlock), new PropertyMetadata(-1, OnActiveWordIndexChanged));

    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight), typeof(double), typeof(KaraokeTextBlock), new PropertyMetadata(24.0));

    public static readonly DependencyProperty LineTextAlignmentProperty = DependencyProperty.Register(
        nameof(LineTextAlignment), typeof(TextAlignment), typeof(KaraokeTextBlock), new PropertyMetadata(TextAlignment.Center));

    private readonly TextBlock _textBlock = new() { TextWrapping = TextWrapping.Wrap, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
    private List<Run>? _runs;
    private int _wordRunOffset; // 1 when a prelude run precedes the word runs, else 0
    private int _wordRunCount;
    private Brush? _sungBrush;
    private Brush? _currentBrush;
    private Brush? _upcomingBrush;

    public KaraokeTextBlock()
    {
        Content = _textBlock;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;

        // Typography flows from the control (bound by the template) into the inner TextBlock;
        // Control and TextBlock font properties do not inherit across that boundary.
        _textBlock.SetBinding(TextBlock.FontSizeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(FontSize)) });
        _textBlock.SetBinding(TextBlock.FontFamilyProperty, new Binding { Source = this, Path = new PropertyPath(nameof(FontFamily)) });
        _textBlock.SetBinding(TextBlock.CharacterSpacingProperty, new Binding { Source = this, Path = new PropertyPath(nameof(CharacterSpacing)) });
        _textBlock.SetBinding(TextBlock.LineHeightProperty, new Binding { Source = this, Path = new PropertyPath(nameof(LineHeight)) });
        _textBlock.SetBinding(TextBlock.TextAlignmentProperty, new Binding { Source = this, Path = new PropertyPath(nameof(LineTextAlignment)) });

        ActualThemeChanged += (_, _) => Rebuild();
    }

    /// <summary>The exact tag-stripped line text (prelude + concatenated word segments).</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IReadOnlyList<LrcWord>? Words
    {
        get => (IReadOnlyList<LrcWord>?)GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public int ActiveWordIndex
    {
        get => (int)GetValue(ActiveWordIndexProperty);
        set => SetValue(ActiveWordIndexProperty, value);
    }

    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public TextAlignment LineTextAlignment
    {
        get => (TextAlignment)GetValue(LineTextAlignmentProperty);
        set => SetValue(LineTextAlignmentProperty, value);
    }

    private static void OnWordsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KaraokeTextBlock)d).Rebuild();

    private static void OnActiveWordIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KaraokeTextBlock)d).ApplyActiveWordIndex((int)e.NewValue, (int)e.OldValue);

    private void Rebuild()
    {
        _runs = null;
        _wordRunOffset = 0;
        _wordRunCount = 0;
        _sungBrush = ThemeResourceHelper.GetBrush("TextPrimaryBrush");
        _currentBrush = ThemeResourceHelper.GetBrush("DawnAccentTextBrush");
        _upcomingBrush = ThemeResourceHelper.GetBrush("TextTertiaryBrush");

        _textBlock.Inlines.Clear();
        var words = Words;
        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Degenerate shapes (foreign construction where Text is not prelude+segments) render as
        // plain text rather than emitting a wrong karaoke split.
        int wordChars = 0;
        if (words is { Count: > 0 })
        {
            foreach (var w in words) wordChars += w.Text.Length;
        }
        if (words is not { Count: > 0 } || wordChars > text.Length)
        {
            AddRun(_sungBrush!, text);
            return;
        }

        int preludeLength = text.Length - wordChars;
        if (preludeLength > 0)
        {
            AddRun(_sungBrush!, text[..preludeLength]);
            _wordRunOffset = 1;
        }
        foreach (var w in words)
        {
            AddRun(_upcomingBrush!, w.Text);
        }
        _wordRunCount = words.Count;

        ApplyActiveWordIndex(ActiveWordIndex, -1);
    }

    private void AddRun(Brush brush, string text)
    {
        var run = new Run { Text = text, Foreground = brush };
        _textBlock.Inlines.Add(run);
        (_runs ??= new List<Run>()).Add(run);
    }

    /// <summary>
    /// Mirrors the timeline: the previously active word becomes "sung", the new one "current".
    /// A reset to -1 (line deactivated) paints every word upcoming again — one pass per line
    /// change, still no rebuild.
    /// </summary>
    private void ApplyActiveWordIndex(int newIndex, int oldIndex)
    {
        var runs = _runs;
        if (runs is not { Count: > 0 } || _wordRunCount == 0) return;

        if (newIndex > _wordRunCount - 1) newIndex = _wordRunCount - 1;
        if (newIndex < -1) newIndex = -1;
        if (newIndex == oldIndex) return;

        if (oldIndex >= 0 && oldIndex < _wordRunCount)
        {
            runs[_wordRunOffset + oldIndex].Foreground = _sungBrush!;
        }

        if (newIndex >= 0)
        {
            runs[_wordRunOffset + newIndex].Foreground = _currentBrush!;
        }
        else
        {
            // deactivated: every word back to upcoming
            for (int i = 0; i < _wordRunCount; i++)
            {
                runs[_wordRunOffset + i].Foreground = _upcomingBrush!;
            }
        }
    }
}

/// <summary>
/// Picks the word-highlight template for lines carrying enhanced-LRC word timings when the
/// karaoke setting is on; everything else uses the normal template. HasWords is fixed per track
/// load, so virtualization recycling never needs to swap a realized container's template.
/// </summary>
public sealed class LyricsLineTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NormalTemplate { get; set; }
    public DataTemplate? KaraokeTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        item is LrcLineVm { UseKaraoke: true } ? KaraokeTemplate : NormalTemplate;

    protected override DataTemplate? SelectTemplateCore(object item) =>
        SelectTemplateCore(item, null!);
}
