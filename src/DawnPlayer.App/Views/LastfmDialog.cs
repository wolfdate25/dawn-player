using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DawnPlayer.App.Views;

/// <summary>
/// Last.fm account dialog: user-supplied API credentials, the two-step browser auth, and the
/// scrobble enable toggle with live status. The token from step 1 is kept in the dialog until
/// confirmed, matching how the flow must run (token → browser approval → session exchange).
/// </summary>
public static class LastfmDialog
{
    private static string? _pendingToken;

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var scrobbler = AppServices.Scrobbler;
        var settings = AppServices.Settings.Lastfm;

        var apiKey = new TextBox { Text = settings.ApiKey, PlaceholderText = "api key", FontSize = 12.5 };
        var apiSecret = new TextBox { Text = settings.ApiSecret, PlaceholderText = "shared secret", FontSize = 12.5 };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var authButton = new Button { FontSize = 12 };
        var confirmButton = new Button { FontSize = 12, Visibility = Visibility.Collapsed };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        void RefreshStatus()
        {
            if (!scrobbler.IsConfigured)
            {
                status.Text = AppStrings.Get("Lastfm_StatusNeedKeys", "Last.fm API 키와 비밀을 입력하세요 (last.fm/api 에서 생성).");
                authButton.Visibility = Visibility.Collapsed;
            }
            else if (scrobbler.IsAuthenticated)
            {
                status.Text = AppStrings.Format(
                    "Lastfm_StatusAuthed", "{0} 로 인증됨 · 대기 중인 스러블 {1}개",
                    string.IsNullOrEmpty(scrobbler.Username) ? "?" : scrobbler.Username, scrobbler.QueuedCount);
                authButton.Content = AppStrings.Get("Lastfm_Reauth", "다시 인증");
                authButton.Visibility = Visibility.Visible;
            }
            else
            {
                status.Text = AppStrings.Get("Lastfm_StatusNotAuthed", "브라우저에서 인증하면 스러블이 켜집니다.");
                authButton.Content = AppStrings.Get("Lastfm_StartAuth", "브라우저에서 인증");
                authButton.Visibility = Visibility.Visible;
            }
        }

        authButton.Click += async (_, _) =>
        {
            try
            {
                scrobbler.SetCredentials(apiKey.Text, apiSecret.Text);
                authButton.IsEnabled = false;
                _pendingToken = await scrobbler.StartAuthAsync();
                var psi = new System.Diagnostics.ProcessStartInfo(
                    LastfmClient.BuildAuthPageUrl(AppServices.Settings.Lastfm.ApiKey, _pendingToken))
                {
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
                status.Text = AppStrings.Get("Lastfm_AuthPending", "브라우저에서 권한을 허용한 뒤 [인증 완료]를 누르세요.");
                confirmButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                status.Text = AppStrings.Format("Lastfm_AuthFailed", "인증 실패: {0}", ex.Message);
            }
            finally
            {
                authButton.IsEnabled = true;
            }
        };

        confirmButton.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(_pendingToken)) return;
            try
            {
                confirmButton.IsEnabled = false;
                string username = await scrobbler.CompleteAuthAsync(_pendingToken);
                _pendingToken = null;
                confirmButton.Visibility = Visibility.Collapsed;
                status.Text = AppStrings.Format(
                    "Lastfm_AuthDone", "{0} 로 인증되었습니다. 스러블이 켜졌습니다.", username);
            }
            catch (Exception ex)
            {
                status.Text = AppStrings.Format("Lastfm_AuthFailed", "인증 실패: {0}", ex.Message);
            }
            finally
            {
                confirmButton.IsEnabled = true;
                RefreshStatus();
            }
        };

        var enableToggle = new ToggleSwitch
        {
            OnContent = AppStrings.Get("Lastfm_EnabledOn", "스러블링 켬"),
            OffContent = AppStrings.Get("Lastfm_EnabledOff", "꺼짐"),
            IsOn = settings.Enabled,
            FontSize = 12.5,
        };
        enableToggle.Toggled += (_, _) =>
        {
            scrobbler.SetCredentials(apiKey.Text, apiSecret.Text);
            scrobbler.SetEnabled(enableToggle.IsOn);
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(apiKey);
        panel.Children.Add(apiSecret);
        panel.Children.Add(buttons);
        panel.Children.Add(enableToggle);
        panel.Children.Add(status);
        buttons.Children.Add(authButton);
        buttons.Children.Add(confirmButton);

        RefreshStatus();

        var dialog = new ContentDialog
        {
            Title = AppStrings.Get("Lastfm_Title", "Last.fm 스러블링"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = AppStrings.Get("Common_Close", "닫기"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        // Credentials save on close too (typing then closing should not lose the keys).
        dialog.Closed += (_, _) => scrobbler.SetCredentials(apiKey.Text, apiSecret.Text);

        await dialog.ShowAsync();
    }
}
