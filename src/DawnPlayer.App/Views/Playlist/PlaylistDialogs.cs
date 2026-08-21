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
        ".mp3", ".aac", ".m4a", ".m4b", ".mp4", ".flac", ".ogg", ".oga", ".wav", ".alac"
    };

    /// <summary>
    /// Shows a modal dialog to rename the given playlist.
    /// </summary>
    public static async Task<bool> ShowRenameDialogAsync(Playlist pl, XamlRoot xamlRoot, PlaylistManager playlists)
    {
        var box = new TextBox { Text = pl.Name, Header = "재생목록 이름" };
        var dialog = new ContentDialog
        {
            Title = "재생목록 이름 변경",
            Content = box,
            PrimaryButtonText = "확인",
            CloseButtonText = "취소",
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
    /// Opens a save file picker to export the given playlist as an M3U8 file.
    /// </summary>
    public static async Task ExportPlaylistAsync(Playlist pl, IntPtr windowHandle)
    {
        var picker = new FileSavePicker { SuggestedFileName = pl.Name };
        picker.FileTypeChoices.Add("M3U8 재생목록", new List<string> { ".m3u8" });
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
                AppServices.RaiseWarning($"저장 실패: {ex.Message}");
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
