using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DawnPlayer.App.Views;

/// <summary>
/// Modal dialog for multi-format audio transcoding (WAV natively, or FLAC/MP3/Opus/AAC via ffmpeg).
/// </summary>
public static class TranscodeDialog
{
    public static async Task ShowAsync(IReadOnlyList<Track> tracks, XamlRoot root)
    {
        if (tracks == null || tracks.Count == 0) return;

        var validTracks = tracks.Where(t => t != null && t.SourceKind == TrackSourceKind.File && !t.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        if (validTracks.Count == 0)
        {
            AppServices.RaiseWarning(AppStrings.Get("Convert_NoValidFiles", "변환 가능한 로컬 파일 트랙이 없습니다 (스트림/라디오 제외)."));
            return;
        }

        // Determine default output folder: directory of first file or Downloads
        string defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        try
        {
            var firstDir = Path.GetDirectoryName(validTracks[0].Path);
            if (!string.IsNullOrEmpty(firstDir) && Directory.Exists(firstDir))
                defaultFolder = firstDir;
        }
        catch { }

        var form = new StackPanel { Spacing = 12, MinWidth = 360 };

        // 1. Output format selector
        var formatBox = new ComboBox
        {
            Header = AppStrings.Get("Convert_FormatHeader", "출력 형식"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        formatBox.Items.Add(new ComboBoxItem { Content = "WAV (.wav) — 무손실 비압축 PCM", Tag = OutputAudioFormat.Wav });
        formatBox.Items.Add(new ComboBoxItem { Content = "FLAC (.flac) — 무손실 압축", Tag = OutputAudioFormat.Flac });
        formatBox.Items.Add(new ComboBoxItem { Content = "MP3 (.mp3) — 범용 고음질", Tag = OutputAudioFormat.Mp3 });
        formatBox.Items.Add(new ComboBoxItem { Content = "Opus (.opus) — 고효율 음성/음악", Tag = OutputAudioFormat.Opus });
        formatBox.Items.Add(new ComboBoxItem { Content = "AAC (.m4a) — 모바일/Apple 호환", Tag = OutputAudioFormat.Aac });

        // 2. Quality preset selector
        var qualityBox = new ComboBox
        {
            Header = AppStrings.Get("Convert_QualityHeader", "품질 / 비트레이트"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };

        void RefreshQualityPresets(OutputAudioFormat fmt)
        {
            qualityBox.Items.Clear();
            switch (fmt)
            {
                case OutputAudioFormat.Wav:
                    qualityBox.Items.Add(new ComboBoxItem { Content = "16-bit PCM (표준 CD 품질)", Tag = "16" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "24-bit PCM (고해상도)", Tag = "24" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "32-bit Float", Tag = "32" });
                    break;
                case OutputAudioFormat.Flac:
                    qualityBox.Items.Add(new ComboBoxItem { Content = "Level 5 (기본 권장 압축)", Tag = "5" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "Level 8 (최대 압축)", Tag = "8" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "Level 0 (빠른 압축)", Tag = "0" });
                    break;
                case OutputAudioFormat.Mp3:
                    qualityBox.Items.Add(new ComboBoxItem { Content = "320 kbps (CBR 최상 음질)", Tag = "320k" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "V0 (VBR 고품질 ~245 kbps)", Tag = "V0" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "256 kbps", Tag = "256k" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "192 kbps", Tag = "192k" });
                    break;
                case OutputAudioFormat.Opus:
                    qualityBox.Items.Add(new ComboBoxItem { Content = "160 kbps (음악 최상 음질)", Tag = "160k" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "128 kbps (투명 음질 권장)", Tag = "128k" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "96 kbps (고효율)", Tag = "96k" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "192 kbps", Tag = "192k" });
                    break;
                case OutputAudioFormat.Aac:
                    qualityBox.Items.Add(new ComboBoxItem { Content = "256 kbps (iTunes Plus 수준)", Tag = "256k" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "192 kbps", Tag = "192k" });
                    qualityBox.Items.Add(new ComboBoxItem { Content = "128 kbps", Tag = "128k" });
                    break;
            }
            qualityBox.SelectedIndex = 0;
        }

        RefreshQualityPresets(OutputAudioFormat.Wav);

        // 3. Output directory picker
        var dirBox = new TextBox
        {
            Header = AppStrings.Get("Convert_DirHeader", "저장 폴더"),
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
                dirBox.Text = folder;
            }
        };

        // 4. Options checkboxes
        var rgCheck = new CheckBox
        {
            Content = AppStrings.Get("Convert_ApplyRg", "ReplayGain 음량 굽기 (기기 음량 평준화)"),
            IsChecked = false
        };
        var tagCheck = new CheckBox
        {
            Content = AppStrings.Get("Convert_WriteTags", "메타데이터 및 앨범 커버 복사"),
            IsChecked = true
        };

        // 5. Ffmpeg detection notice — shows the resolved state instead of a static hint, so a
        // missing binary is known before the batch starts rather than N failures after.
        var ffmpegNotice = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        void UpdateFfmpegNotice(OutputAudioFormat fmt)
        {
            if (fmt == OutputAudioFormat.Wav)
            {
                ffmpegNotice.Visibility = Visibility.Collapsed;
                return;
            }

            ffmpegNotice.Visibility = Visibility.Visible;
            if (AudioTranscoder.IsFfmpegAvailable())
            {
                ffmpegNotice.Text = AppStrings.Get("Convert_FfmpegAvailable", "ffmpeg 감지됨 — FLAC/MP3/Opus/AAC 변환 가능");
                ffmpegNotice.Foreground = Helpers.ThemeResourceHelper.GetBrush("TextSecondaryBrush");
            }
            else
            {
                ffmpegNotice.Text = AppStrings.Get("Convert_FfmpegMissing", "ffmpeg 미감지 — WAV 변환만 가능합니다. PATH에 ffmpeg를 추가하면 다른 형식도 변환할 수 있습니다.");
                ffmpegNotice.Foreground = Helpers.ThemeResourceHelper.GetBrush("StatusDangerBrush");
            }
        }

        formatBox.SelectionChanged += (_, _) =>
        {
            if (formatBox.SelectedItem is ComboBoxItem item && item.Tag is OutputAudioFormat fmt)
            {
                RefreshQualityPresets(fmt);
                UpdateFfmpegNotice(fmt);
            }
        };

        form.Children.Add(formatBox);
        form.Children.Add(qualityBox);
        form.Children.Add(dirBox);
        form.Children.Add(pickDirButton);
        form.Children.Add(rgCheck);
        form.Children.Add(tagCheck);
        form.Children.Add(ffmpegNotice);

        var dialog = new ContentDialog
        {
            Title = AppStrings.Format("Convert_Title", "{0}개 트랙 오디오 변환", validTracks.Count),
            Content = form,
            PrimaryButtonText = AppStrings.Get("Convert_StartButton", "변환 시작"),
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var selectedFormat = (formatBox.SelectedItem as ComboBoxItem)?.Tag as OutputAudioFormat? ?? OutputAudioFormat.Wav;
        var selectedQuality = (qualityBox.SelectedItem as ComboBoxItem)?.Tag as string;
        string destDir = dirBox.Text;
        bool applyRg = rgCheck.IsChecked == true;
        bool writeTags = tagCheck.IsChecked == true;
        short bits = 16;
        if (selectedFormat == OutputAudioFormat.Wav && short.TryParse(selectedQuality, out var b))
            bits = b;

        var options = new TranscodeOptions
        {
            OutputDirectory = destDir,
            OutputFormat = selectedFormat,
            Quality = selectedQuality,
            ApplyReplayGain = applyRg,
            WriteTags = writeTags,
            BitsPerSample = bits
        };

        // Modal progress dialog with a live overall fraction and a cancel button — a long batch
        // used to run fire-and-forget with no feedback and no way back out.
        int total = validTracks.Count;
        var statusText = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Helpers.ThemeResourceHelper.GetBrush("TextSecondaryBrush")
        };
        var progressBar = new ProgressBar { Minimum = 0, Maximum = 1 };
        var progressForm = new StackPanel { Spacing = 10, MinWidth = 360 };
        progressForm.Children.Add(progressBar);
        progressForm.Children.Add(statusText);

        var cts = new CancellationTokenSource();
        var progressDialog = new ContentDialog
        {
            Title = AppStrings.Format("Convert_ProgressTitle", "{0}개 트랙 변환 중…", total),
            Content = progressForm,
            CloseButtonText = AppStrings.Get("Common_Cancel", "취소"),
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            XamlRoot = root
        };
        progressDialog.CloseButtonClick += (_, _) => cts.Cancel();

        var dispatcher = progressDialog.DispatcherQueue;
        int done = 0, ok = 0, failed = 0;

        var runner = Task.Run(async () =>
        {
            try
            {
                foreach (var track in validTracks)
                {
                    if (cts.IsCancellationRequested) break;

                    string name = System.IO.Path.GetFileName(track.Path);
                    dispatcher.TryEnqueue(() => statusText.Text =
                        AppStrings.Format("Convert_ProgressTrack", "{0} / {1} — {2}", done + 1, total, name));

                    int lastPercent = -1;
                    try
                    {
                        var output = AudioTranscoder.Convert(track, options, out var r,
                            progress: f =>
                            {
                                int pct = (int)Math.Round((done + f) / total * 100.0);
                                if (pct != lastPercent)
                                {
                                    lastPercent = pct;
                                    dispatcher.TryEnqueue(() => progressBar.Value = pct / 100.0);
                                }
                            },
                            cts.Token);
                        if (r == TranscodeResult.Ok && output != null) ok++;
                        else failed++;
                    }
                    catch (OperationCanceledException)
                    {
                        // 부분 파일은 Convert 내부에서 삭제된다. 남은 트랙은 루프 탈출로 건너뛴다.
                    }
                    catch (Exception ex)
                    {
                        App.Log($"[transcode] {track.Path}: {ex.Message}");
                        failed++;
                    }

                    done++;
                    dispatcher.TryEnqueue(() => progressBar.Value = (double)done / total);
                }
            }
            finally
            {
                int processed = ok + failed;
                dispatcher.TryEnqueue(() =>
                {
                    progressBar.Value = 1;
                    try { progressDialog.Hide(); } catch { }
                });
                if (processed < total)
                {
                    AppServices.RaiseWarning(AppStrings.Format(
                        "Convert_Cancelled", "변환 취소: {0}개 완료, {1}개 실패, {2}개 미처리", ok, failed, total - processed));
                }
                else
                {
                    AppServices.RaiseWarning(AppStrings.Format(
                        "Convert_Done", "변환 완료: {0}개 성공, {1}개 실패", ok, failed));
                }
            }
        });

        await progressDialog.ShowAsync();
        cts.Cancel(); // 닫힘 경로가 CloseButtonClick 외(ESC 등)여도 루프가 멈추도록
        await runner; // finally의 완료 토스트까지 마치고 반환
    }
}
