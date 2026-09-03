using System;
using System.Collections.Generic;
using System.Globalization;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DawnPlayer.App.Views;

/// <summary>
/// Listening report dashboard: period selector (all time / 30 days / this year) over the
/// play-event history, with top artists, albums, genres and tracks plus totals. Built in code on
/// the same ContentDialog pattern as the tag editor; every string goes through AppStrings.
/// </summary>
public static class ListeningReportDialog
{
    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var reportHost = new ContentControl();
        var periodCombo = new ComboBox { Header = AppStrings.Get("Report_PeriodHeader", "기간"), MinWidth = 180, FontSize = 12.5 };
        periodCombo.Items.Add(AppStrings.Get("Report_PeriodAllTime", "전체"));
        periodCombo.Items.Add(AppStrings.Get("Report_Period30Days", "최근 30일"));
        periodCombo.Items.Add(AppStrings.Get("Report_PeriodThisYear", "올해"));
        periodCombo.SelectedIndex = 1;
        periodCombo.SelectionChanged += async (_, _) =>
        {
            try
            {
                reportHost.Content = await BuildAsync((AppServices.ReportPeriod)periodCombo.SelectedIndex);
            }
            catch (Exception ex)
            {
                App.Log($"[ListeningReport] build failed: {ex}");
            }
        };

        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Report_Title", "청취 리포트"),
            PrimaryButtonText = AppStrings.Get("Common_Close", "닫기"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 460, MaxWidth = 560 };
        panel.Children.Add(periodCombo);
        panel.Children.Add(reportHost);
        dialog.Content = panel;

        // The enum values line up with the combo indices on purpose; the cast reads like a hack
        // otherwise. AllTime=0, Last30Days=1 (the default view), ThisYear=2.
        reportHost.Content = await BuildAsync(AppServices.ReportPeriod.Last30Days);

        await dialog.ShowAsync();
    }

    private static async Task<UIElement> BuildAsync(AppServices.ReportPeriod period)
    {
        var report = await AppServices.BuildListeningReportAsync(period);

        var summary = new Grid { ColumnSpacing = 18 };
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        summary.AddChild(MakeStat(AppStrings.Get("Report_TotalPlays", "총 재생"),
            report.TotalPlays.ToString("N0", CultureInfo.CurrentCulture)), 0);
        summary.AddChild(MakeStat(AppStrings.Get("Report_UniqueTracks", "재생한 곡"),
            report.UniqueTracks.ToString("N0", CultureInfo.CurrentCulture)), 1);
        summary.AddChild(MakeStat(AppStrings.Get("Report_ListenTime", "청취 시간"),
            FormatDuration(report.TotalListenTime)), 2);

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(summary);
        AddSection(body, AppStrings.Get("Report_TopArtists", "많이 들은 아티스트"), report.TopArtists, 5);
        AddSection(body, AppStrings.Get("Report_TopAlbums", "많이 들은 앨범"), report.TopAlbums, 5);
        AddSection(body, AppStrings.Get("Report_TopGenres", "장르"), report.TopGenres, 5);
        AddSection(body, AppStrings.Get("Report_TopTracks", "많이 들은 곡"), report.TopTracks, 10);

        if (report.TotalPlays == 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = AppStrings.Get("Report_Empty", "이 기간에 기록된 재생이 없습니다."),
                FontSize = 12,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8),
            });
        }

        var scroller = new ScrollViewer
        {
            Content = body,
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        return scroller;
    }

    private static void AddSection(StackPanel parent, string header,
        IReadOnlyList<ListeningReportEntry> entries, int maxRows)
    {
        if (entries.Count == 0) return;

        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            Margin = new Thickness(0, 6, 0, 2),
        });

        int rows = Math.Min(maxRows, entries.Count);
        for (int i = 0; i < rows; i++)
        {
            var e = entries[i];
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.AddChild(new TextBlock
            {
                Text = e.Name,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            }, 0);
            row.AddChild(new TextBlock
            {
                Text = FormatDuration(e.ListenTime),
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
            }, 1);
            row.AddChild(new TextBlock
            {
                // "N회" — the count rides a localized format so the unit word translates.
                Text = AppStrings.Format("Report_PlaysFormat", "{0}회", e.Plays),
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["DawnAccentBrush"],
            }, 2);

            panel.Children.Add(row);
        }

        parent.Children.Add(panel);
    }

    private static StackPanel MakeStat(string label, string value)
    {
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
        };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
        };
        return new StackPanel { Spacing = 1, Children = { valueText, labelText } };
    }

    private static string FormatDuration(TimeSpan t)
    {
        // Digits and separators only — no culture-sensitive formatting to fight with.
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }
}

/// <summary>Grid extension used by the report layout: AddChild(element, column).</summary>
internal static class GridExtensions
{
    public static void AddChild(this Grid grid, FrameworkElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }
}
