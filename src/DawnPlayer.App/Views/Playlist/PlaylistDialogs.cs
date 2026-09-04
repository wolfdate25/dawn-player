using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Playlists;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DawnPlayer.App.Views;

/// <summary>
/// Dialog and file/folder picker helpers for playlist operations with native window interop.
/// </summary>
public static class PlaylistDialogs
{
    private static readonly string[] AudioExtensions =
    {
        ".mp3", ".aac", ".m4a", ".m4b", ".mp4", ".flac", ".ogg", ".oga", ".opus", ".wav", ".alac", ".dsf"
    };

    /// <summary>
    /// Shows a modal dialog to rename the given playlist.
    /// </summary>
    public static async Task<bool> ShowRenameDialogAsync(Playlist pl, XamlRoot xamlRoot, PlaylistManager playlists)
    {
        var box = new TextBox { Text = pl.Name, Header = AppStrings.Get("Msg_PlaylistName", "재생목록 이름") };
        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Msg_RenamePlaylistTitle", "재생목록 이름 변경"),
            Content = box,
            PrimaryButtonText = AppStrings.Get("Common_OK", "확인"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0)
        {
            playlists.RenamePlaylist(pl, box.Text.Trim());
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a query-driven smart playlist: name + foobar-style query, validated in the dialog
    /// before it can be submitted. On success the playlist is created, persisted via the manager's
    /// change event, and selected by the caller.
    /// </summary>
    public static async Task<Playlist?> ShowCreateSmartDialogAsync(XamlRoot xamlRoot, PlaylistManager playlists)
    {
        var (dialog, nameBox, queryBox, errorText) = BuildSmartDialog(
            AppStrings.Get("Smart_Dialog_CreateTitle", "스마트 재생목록 만들기"),
            name: "", query: "", xamlRoot);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var pl = playlists.AddUserSmartPlaylist(nameBox.Text.Trim(), queryBox.Text, out var error);
        if (pl == null) AppServices.RaiseWarning(AppStrings.Format("Smart_Dialog_InvalidFormat", "쿼리를 적용할 수 없습니다: {0}", error ?? ""));
        return pl;
    }

    /// <summary>Edits the name and query of an existing user smart playlist.</summary>
    public static async Task<bool> ShowEditSmartDialogAsync(Playlist pl, XamlRoot xamlRoot, PlaylistManager playlists)
    {
        var (dialog, nameBox, queryBox, errorText) = BuildSmartDialog(
            AppStrings.Format("Smart_Dialog_EditTitle", "{0} 편집", pl.Name),
            name: pl.Name, query: pl.SmartQuery ?? "", xamlRoot);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        bool ok = playlists.TryUpdateUserSmartPlaylist(pl, nameBox.Text.Trim(), queryBox.Text, out var error);
        if (!ok) AppServices.RaiseWarning(AppStrings.Format("Smart_Dialog_InvalidFormat", "쿼리를 적용할 수 없습니다: {0}", error ?? ""));
        return ok;
    }

    private static (ContentDialog Dialog, TextBox NameBox, TextBox QueryBox, TextBlock ErrorText) BuildSmartDialog(
        string title, string name, string query, XamlRoot xamlRoot)
    {
        var nameBox = new TextBox
        {
            Text = name,
            Header = AppStrings.Get("Smart_Dialog_NameHeader", "이름"),
            FontSize = 13,
        };
        var queryBox = new TextBox
        {
            Text = query,
            Header = AppStrings.Get("Smart_Dialog_QueryHeader", "쿼리 (foobar2000 스타일)"),
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 140,
            PlaceholderText = "%rating% GREATER 3 AND %genre% HAS jazz",
        };
        var errorText = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
            Visibility = Visibility.Collapsed,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(nameBox);
        panel.Children.Add(queryBox);
        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.Get("Smart_Dialog_Help",
                "필드: %title% %artist% %album% %genre% %rating% %play_count% %last_play% %first_seen% 등\n" +
                "연산자: IS / != / HAS / GREATER / LESS / MISSING / PRESENT / AND / OR / NOT / ( )\n" +
                "예: %rating% GREATER 3 AND %last_played% DURING LAST 30 DAYS LIMIT 50"),
            FontSize = 11,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = AppStrings.Get("Common_OK", "확인"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        // Live validation: an invalid query keeps the primary button disabled and shows why.
        void Validate()
        {
            bool valid = Core.Playlists.SmartPlaylistQuery.TryParse(queryBox.Text, out _, out var error);
            errorText.Text = error ?? "";
            errorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = valid && nameBox.Text.Trim().Length > 0;
        }

        queryBox.TextChanged += (_, _) => Validate();
        nameBox.TextChanged += (_, _) => Validate();
        dialog.Loaded += (_, _) => Validate();
        dialog.Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(nameBox.Text)) nameBox.Focus(FocusState.Programmatic);
        };

        return (dialog, nameBox, queryBox, errorText);
    }

    /// <summary>
    /// Opens a save file picker to export the given playlist as an M3U8 file.
    /// </summary>
    public static async Task ExportPlaylistAsync(Playlist pl, IntPtr windowHandle)
    {
        var picker = new FileSavePicker { SuggestedFileName = pl.Name };
        picker.FileTypeChoices.Add(AppStrings.Get("Msg_M3U8FileType", "M3U8 재생목록"), new List<string> { ".m3u8" });
        InitializeWithWindow.Initialize(picker, windowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            try
            {
                M3u.Write(file.Path, pl.GetSnapshot(), pl.Name);
            }
            catch (Exception ex)
            {
                AppServices.RaiseWarning(AppStrings.Format("Msg_SaveFailed", ex.Message));
            }
        }
    }

    /// <summary>
    /// Opens a file open picker to select audio files.
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickAudioFilesAsync(IntPtr windowHandle)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, windowHandle);

        foreach (var ext in AudioExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }
        picker.FileTypeFilter.Add("*");

        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// Opens a folder picker to select a music folder.
    /// </summary>
    public static async Task<string?> PickMusicFolderAsync(IntPtr windowHandle)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        InitializeWithWindow.Initialize(picker, windowHandle);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>
    /// Opens a file open picker to select an M3U / M3U8 playlist file.
    /// </summary>
    public static async Task<string?> PickPlaylistFileAsync(IntPtr windowHandle)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, windowHandle);
        picker.FileTypeFilter.Add(".m3u8");
        picker.FileTypeFilter.Add(".m3u");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
