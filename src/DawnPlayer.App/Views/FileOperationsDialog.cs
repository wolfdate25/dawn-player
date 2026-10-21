using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Library;
using DawnPlayer.Core.Models;
using DawnPlayer.Core.Util;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DawnPlayer.App.Views;

/// <summary>
/// Modal dialog for file organization, batch renaming, and relocating with live preview and library synchronization.
/// </summary>
public static class FileOperationsDialog
{
    public static async Task ShowAsync(IReadOnlyList<Track> tracks, XamlRoot root)
    {
        if (tracks == null || tracks.Count == 0) return;

        var validTracks = tracks.Where(t => t != null && t.SourceKind == TrackSourceKind.File && !t.Path.Contains("#cue=")).ToList();
        if (validTracks.Count == 0)
        {
            AppServices.RaiseWarning(AppStrings.Get("FileOps_NoValidFiles", "정리 가능한 로컬 파일 트랙이 없습니다 (스트림/CUE 가상 트랙 제외)."));
            return;
        }

        string defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        try
        {
            var firstPhysical = AppPaths.PhysicalPath(validTracks[0].Path);
            var firstDir = Path.GetDirectoryName(firstPhysical);
            if (!string.IsNullOrEmpty(firstDir) && Directory.Exists(firstDir))
                defaultFolder = firstDir;
        }
        catch { }

        var form = new StackPanel { Spacing = 10, MinWidth = 520, MaxWidth = 640 };

        // 1. Base Folder Picker
        var baseDirBox = new TextBox
        {
            Header = AppStrings.Get("FileOps_BaseDirHeader", "대상 기준 폴더"),
            Text = defaultFolder,
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var pickDirButton = new Button
        {
            Content = AppStrings.Get("Convert_BrowseFolder", "폴더 변경…"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        pickDirButton.Click += async (_, _) =>
        {
            var folder = await PlaylistDialogs.PickMusicFolderAsync(AppServices.MainWindowHandle);
            if (!string.IsNullOrEmpty(folder))
            {
                baseDirBox.Text = folder;
            }
        };

        // 2. Pattern Selector / Input
        var patternBox = new ComboBox
        {
            Header = AppStrings.Get("FileOps_PatternHeader", "이름/경로 패턴"),
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        patternBox.Items.Add(new ComboBoxItem { Content = "%artist% - %title%" });
        patternBox.Items.Add(new ComboBoxItem { Content = "[%tracknumber% - ]%title%" });
        patternBox.Items.Add(new ComboBoxItem { Content = "%artist%/[%year% - ]%album%/[%tracknumber% - ]%title%" });
        patternBox.Items.Add(new ComboBoxItem { Content = "%genre%/%artist%/%album%/[%tracknumber% - ]%title%" });
        patternBox.SelectedIndex = 0;

        // 3. Mode & Collision Policy
        var optionsRow = new Grid { ColumnSpacing = 12 };
        optionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        optionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var modeBox = new ComboBox
        {
            Header = AppStrings.Get("FileOps_ModeHeader", "작업 모드"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        modeBox.Items.Add(new ComboBoxItem { Content = AppStrings.Get("FileOps_ModeMove", "파일 이동 및 이름 변경 (라이브러리 동기화)"), Tag = FileOpMode.Move });
        modeBox.Items.Add(new ComboBoxItem { Content = AppStrings.Get("FileOps_ModeCopy", "새 위치로 복사"), Tag = FileOpMode.Copy });
        Grid.SetColumn(modeBox, 0);

        var collisionBox = new ComboBox
        {
            Header = AppStrings.Get("FileOps_CollisionHeader", "충돌 해결 정책"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        collisionBox.Items.Add(new ComboBoxItem { Content = AppStrings.Get("FileOps_CollisionAuto", "자동 번호 부여 (파일명 (1))"), Tag = CollisionPolicy.AutoRename });
        collisionBox.Items.Add(new ComboBoxItem { Content = AppStrings.Get("FileOps_CollisionSkip", "건너뛰기"), Tag = CollisionPolicy.Skip });
        collisionBox.Items.Add(new ComboBoxItem { Content = AppStrings.Get("FileOps_CollisionOverwrite", "덮어쓰기"), Tag = CollisionPolicy.Overwrite });
        Grid.SetColumn(collisionBox, 1);

        optionsRow.Children.Add(modeBox);
        optionsRow.Children.Add(collisionBox);

        // 4. Preview Section
        var previewHeader = new TextBlock
        {
            Text = AppStrings.Get("FileOps_PreviewHeader", "변경 미리보기"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var previewList = new ListView
        {
            Height = 180,
            SelectionMode = ListViewSelectionMode.None,
            BorderThickness = new Thickness(1),
            BorderBrush = Helpers.ThemeResourceHelper.GetBrush("ControlStrokeColorDefaultBrush"),
            CornerRadius = new CornerRadius(4)
        };

        IReadOnlyList<FileOpPlan> currentPlans = [];

        void RefreshPreview()
        {
            string baseDir = baseDirBox.Text;
            string pattern = patternBox.Text;
            if (string.IsNullOrWhiteSpace(pattern) && patternBox.SelectedItem is ComboBoxItem cbi)
                pattern = cbi.Content as string ?? "%artist% - %title%";

            var mode = (modeBox.SelectedItem as ComboBoxItem)?.Tag as FileOpMode? ?? FileOpMode.Move;
            var collision = (collisionBox.SelectedItem as ComboBoxItem)?.Tag as CollisionPolicy? ?? CollisionPolicy.AutoRename;

            try
            {
                currentPlans = FileOperationService.PlanOperations(validTracks, baseDir, pattern, mode, collision);
                previewList.Items.Clear();

                foreach (var p in currentPlans)
                {
                    var itemGrid = new Grid { Padding = new Thickness(4, 2, 4, 2), ColumnSpacing = 8 };
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    string statusIcon = p.Status switch
                    {
                        FileOpStatus.Ready => "✓",
                        FileOpStatus.Skipped => "—",
                        FileOpStatus.Invalid => "✕",
                        _ => "!"
                    };
                    var statusColor = p.Status switch
                    {
                        FileOpStatus.Ready => Colors.ForestGreen,
                        FileOpStatus.Skipped => Colors.Gray,
                        _ => Colors.Crimson
                    };

                    var iconText = new TextBlock { Text = statusIcon, Foreground = new SolidColorBrush(statusColor), FontWeight = Microsoft.UI.Text.FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(iconText, 0);

                    string srcName = Path.GetFileName(p.SourcePath);
                    string relDest = p.DestinationPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                        ? p.DestinationPath[baseDir.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : p.DestinationPath;

                    var descText = new TextBlock
                    {
                        Text = $"{srcName} → {relDest}",
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(descText, 1);

                    itemGrid.Children.Add(iconText);
                    itemGrid.Children.Add(descText);
                    previewList.Items.Add(itemGrid);
                }
            }
            catch { }
        }

        baseDirBox.TextChanged += (_, _) => RefreshPreview();
        patternBox.SelectionChanged += (_, _) => RefreshPreview();
        modeBox.SelectionChanged += (_, _) => RefreshPreview();
        collisionBox.SelectionChanged += (_, _) => RefreshPreview();

        RefreshPreview();

        form.Children.Add(baseDirBox);
        form.Children.Add(pickDirButton);
        form.Children.Add(patternBox);
        form.Children.Add(optionsRow);
        form.Children.Add(previewHeader);
        form.Children.Add(previewList);

        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("FileOps_Title", "파일 정리 및 이름 변경"),
            Content = form,
            PrimaryButtonText = AppStrings.Format("FileOps_ExecuteButton", "실행 ({0}개)", validTracks.Count),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var selectedMode = (modeBox.SelectedItem as ComboBoxItem)?.Tag as FileOpMode? ?? FileOpMode.Move;
        var plansToRun = currentPlans;

        AppServices.RaiseWarning(AppStrings.Format("FileOps_Started", "{0}개 파일 정리를 시작합니다.", plansToRun.Count));

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await FileOperationService.ExecuteAsync(
                    plansToRun,
                    selectedMode,
                    AppServices.Library,
                    AppServices.Playlists);

                AppServices.RaiseWarning(AppStrings.Format(
                    "FileOps_Done",
                    "파일 정리 완료: {0}개 성공, {1}개 실패, {2}개 건너뜀",
                    result.Succeeded, result.Failed, result.Skipped));
            }
            catch (Exception ex)
            {
                App.Log($"[file-ops] {ex.Message}");
            }
        });
    }
}
