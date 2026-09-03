using System;
using System.Collections.Generic;
using System.Linq;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DawnPlayer.App.Views;

/// <summary>
/// Tag editor: a single track's full metadata form, or an album batch form that touches only the
/// shared fields (album artist, album, genre, year). Every write flows through
/// <see cref="TagWriter"/> (atomic swap copies), and saved files are re-read so the library snaps
/// to the edited tags while keeping the rows' listening history.
/// </summary>
public static class TagEditorDialogs
{
    public static async Task<bool> ShowForTrackAsync(Track track, XamlRoot root)
    {
        if (track == null) return false;
        var state = Build(track, multi: false);
        var dialog = BuildDialog(track.Path, root, state, multi: false);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        return Apply(new List<(Track Original, TagEdit Edit)> { (track, CollectTrackEdit(state)) }, root);
    }

    public static async Task<bool> ShowForAlbumAsync(IReadOnlyList<Track> tracks, XamlRoot root)
    {
        var list = tracks?.Where(t => t != null && !string.IsNullOrEmpty(t.Path)).ToList()
                   ?? new List<Track>();
        if (list.Count == 0) return false;

        var first = list[0];
        var state = Build(first, multi: true);
        var dialog = BuildDialog(
            AppStrings.Format("Msg_TagEditorAlbumTitle", "{0}개 트랙 앨범 태그 편집", list.Count), root, state, multi: true);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        var edits = list.Select(t => (t, CollectAlbumEdit(state))).ToList();
        return Apply(edits, root);
    }

    // ---------------- form ----------------

    private sealed class EditorState
    {
        public TextBox? TitleBox;
        public TextBox? ArtistBox;
        public TextBox? AlbumArtistBox;
        public TextBox? AlbumBox;
        public TextBox? GenreBox;
        public TextBox? YearBox;
        public TextBox? TrackNoBox;
        public TextBox? DiscNoBox;
        public Image? ArtPreview;
        public string? StagedArtPath; // set by the change button, applied on save
        public bool RemoveArtStaged;
        public string? OriginalArtPath;

        // Batch mode only: an untouched field must not overwrite the tracks whose value differs
        // from the first one. The boxes are pre-filled with the first track's values as a hint,
        // but only user-edited fields apply.
        public bool ArtistDirty;
        public bool AlbumArtistDirty;
        public bool AlbumDirty;
        public bool GenreDirty;
        public bool YearDirty;

        public static void WatchForBatch(TextBox box, Action mark)
        {
            box.TextChanged += (_, _) => mark();
        }
    }

    private static EditorState Build(Track track, bool multi)
    {
        var state = new EditorState { OriginalArtPath = track.ArtPath };
        state.TitleBox = MakeBox(track.Title);
        state.ArtistBox = MakeBox(track.Artist);
        state.AlbumArtistBox = MakeBox(track.AlbumArtist);
        state.AlbumBox = MakeBox(track.Album);
        state.GenreBox = MakeBox(track.Genre);
        state.YearBox = MakeBox(track.Year > 0 ? track.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        state.TrackNoBox = MakeBox(track.TrackNo > 0 ? track.TrackNo.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        state.DiscNoBox = MakeBox(track.DiscNo > 0 ? track.DiscNo.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
        state.ArtPreview = MakeArtPreview(track.ArtPath);
        return state;
    }

    private static TextBox MakeBox(string text) => new() { Text = text ?? "", FontSize = 13 };

    private static Image MakeArtPreview(string? artPath)
    {
        var image = new Image { Width = 96, Height = 96 };
        if (!string.IsNullOrEmpty(artPath) && System.IO.File.Exists(artPath))
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 192 };
                bitmap.UriSource = new Uri(artPath, UriKind.Absolute);
                image.Source = bitmap;
            }
            catch { }
        }
        return image;
    }

    private static ContentDialog BuildDialog(string title, XamlRoot root, EditorState state, bool multi)
    {
        if (multi)
        {
            EditorState.WatchForBatch(state.ArtistBox!, () => state.ArtistDirty = true);
            EditorState.WatchForBatch(state.AlbumArtistBox!, () => state.AlbumArtistDirty = true);
            EditorState.WatchForBatch(state.AlbumBox!, () => state.AlbumDirty = true);
            EditorState.WatchForBatch(state.GenreBox!, () => state.GenreDirty = true);
            EditorState.WatchForBatch(state.YearBox!, () => state.YearDirty = true);
        }

        var form = new StackPanel { Spacing = 8 };
        string hint = multi
            ? AppStrings.Get("Msg_TagEditorAlbumHint", "공유 필드만 변경됩니다 (앨범·가수·장르·연도). 비워 두면 그대로 둡니다.")
            : AppStrings.Get("Msg_TagEditorSingleHint", "빈 칸은 그대로 둡니다. 저장은 원자적으로 교체됩니다.");
        form.Children.Add(new TextBlock
        {
            Text = hint,
            FontSize = 12, TextWrapping = TextWrapping.Wrap
        });

        if (!multi)
        {
            form.Children.Add(new TextBlock { Text = AppStrings.Get("Msg_TagEditorTrackPath", "파일"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            form.Children.Add(new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            form.Children.Add(LabeledBox(AppStrings.Get("Msg_TagEditorTitle", "제목"), state.TitleBox!));
        }
        form.Children.Add(LabeledBox(AppStrings.Get("Msg_TagEditorArtist", "아티스트"), state.ArtistBox!));
        form.Children.Add(LabeledBox(AppStrings.Get("Msg_TagEditorAlbumArtist", "앨범 아티스트"), state.AlbumArtistBox!));
        form.Children.Add(LabeledBox(AppStrings.Get("Msg_TagEditorAlbum", "앨범"), state.AlbumBox!));
        form.Children.Add(LabeledBox(AppStrings.Get("Msg_TagEditorGenre", "장르"), state.GenreBox!));
        form.Children.Add(LabeledBox(AppStrings.Get("Msg_TagEditorYear", "연도"), state.YearBox!));
        if (!multi)
        {
            form.Children.Add(LabeledBox(AppStrings.Get("Msg_TagEditorTrackNo", "트랙 번호"), state.TrackNoBox!));
            form.Children.Add(LabeledBox(AppStrings.Get("Msg_TagEditorDiscNo", "디스크 번호"), state.DiscNoBox!));
        }

        if (!multi)
        {
            form.Children.Add(new TextBlock { Text = AppStrings.Get("Msg_TagEditorArt", "앨범아트"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 });
            var artRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            artRow.Children.Add(state.ArtPreview);
            var artButtons = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var changeButton = new Button { Content = AppStrings.Get("Msg_TagEditorArtChange", "변경…"), FontSize = 12 };
            changeButton.Click += async (_, _) => await PickArtAsync(state);
            var removeButton = new Button { Content = AppStrings.Get("Msg_TagEditorArtRemove", "제거"), FontSize = 12 };
            removeButton.Click += (_, _) =>
            {
                state.RemoveArtStaged = true;
                state.StagedArtPath = null;
                if (state.ArtPreview != null) state.ArtPreview.Source = null;
            };
            artButtons.Children.Add(changeButton);
            artButtons.Children.Add(removeButton);
            artRow.Children.Add(artButtons);
            form.Children.Add(artRow);
        }

        string dialogTitle = multi
            ? AppStrings.Get("Msg_TagEditorAlbumDialogTitle", "앨범 태그 편집")
            : AppStrings.Get("Msg_TagEditorDialogTitle", "태그 편집");

        return new ContentDialog
        {
            Title = dialogTitle,
            Content = new ScrollViewer { Content = form, MaxHeight = 560 },
            PrimaryButtonText = AppStrings.Get("Common_Save", "저장"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };
    }

    private static StackPanel LabeledBox(string label, TextBox box)
    {
        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        row.Children.Add(box);
        return row;
    }

    private static async Task PickArtAsync(EditorState state)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, AppServices.MainWindowHandle);
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".webp");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        state.StagedArtPath = file.Path;
        state.RemoveArtStaged = false;
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 192 };
            bitmap.UriSource = new Uri(file.Path, UriKind.Absolute);
            if (state.ArtPreview != null) state.ArtPreview.Source = bitmap;
        }
        catch { }
    }

    // ---------------- apply ----------------

    private static TagEdit CollectTrackEdit(EditorState state) => new(
        Title: state.TitleBox?.Text ?? "",
        Artist: state.ArtistBox?.Text ?? "",
        AlbumArtist: state.AlbumArtistBox?.Text ?? "",
        Album: state.AlbumBox?.Text ?? "",
        Genre: state.GenreBox?.Text ?? "",
        Year: ParseInt(state.YearBox?.Text),
        TrackNo: ParseInt(state.TrackNoBox?.Text),
        DiscNo: ParseInt(state.DiscNoBox?.Text),
        Art: state.RemoveArtStaged ? TagEditorArt.Remove
            : !string.IsNullOrEmpty(state.StagedArtPath) ? TagEditorArt.Embed : TagEditorArt.None,
        ArtSourcePath: state.StagedArtPath);

    private static TagEdit CollectAlbumEdit(EditorState state) => new(
        Artist: state.ArtistDirty ? state.ArtistBox?.Text ?? "" : null,
        AlbumArtist: state.AlbumArtistDirty ? state.AlbumArtistBox?.Text ?? "" : null,
        Album: state.AlbumDirty ? state.AlbumBox?.Text ?? "" : null,
        Genre: state.GenreDirty ? state.GenreBox?.Text ?? "" : null,
        Year: state.YearDirty ? ParseInt(state.YearBox?.Text) : null);

    private static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    private static bool Apply(IReadOnlyList<(Track Original, TagEdit Edit)> edits, XamlRoot root)
    {
        var failed = new List<string>();
        var replaced = new List<Track>();

        foreach (var (original, edit) in edits)
        {
            var result = TagWriter.TryApplyAtomic(original.Path, edit);
            if (result != TagWriteResult.Ok)
            {
                failed.Add(original.Path);
                continue;
            }

            // Re-read so the library snaps to the edited tags while the row keeps its listening
            // history and ReplayGain values the edit did not touch.
            var fresh = TagReader.TryRead(original.Path, out var pic);
            if (fresh == null)
            {
                failed.Add(original.Path);
                continue;
            }

            replaced.Add(fresh with
            {
                PlayCount = original.PlayCount,
                SkipCount = original.SkipCount,
                LastPlayedUtcTicks = original.LastPlayedUtcTicks,
                FirstSeenUtcTicks = original.FirstSeenUtcTicks != 0 ? original.FirstSeenUtcTicks : fresh.FirstSeenUtcTicks
            });
        }

        if (replaced.Count > 0)
        {
            AppServices.Library.ReplaceTracks(replaced);
        }

        if (failed.Count > 0)
        {
            AppServices.RaiseWarning(AppStrings.Format("Msg_TagEditorFailedFormat", "태그 저장 실패: {0}개 파일", failed.Count));
        }
        return replaced.Count > 0;
    }
}
