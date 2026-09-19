using System;
using System.Threading.Tasks;
using DawnPlayer.App.Localization;
using DawnPlayer.App.Services;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.ViewModels.Settings;

/// <summary>
/// View model for the Last.fm settings section. Owns the scrobbling auth flow state so the
/// settings page stays a lean code-behind view — and, more importantly, so the pending auth
/// token lives on the app-scoped <see cref="ScrobbleService"/> rather than on the page: the
/// settings page is recreated on every navigation, and a token held there died with it,
/// orphaning a browser approval the user had just granted.
/// </summary>
public sealed class LastfmSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly ScrobbleService _scrobbler;

    public LastfmSettingsViewModel(AppSettings settings, ScrobbleService scrobbler)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _scrobbler = scrobbler ?? throw new ArgumentNullException(nameof(scrobbler));
    }

    public string ApiKey
    {
        get => _settings.Lastfm.ApiKey ?? string.Empty;
        set
        {
            if (_settings.Lastfm.ApiKey != value)
            {
                _settings.Lastfm.ApiKey = value;
                OnPropertyChanged();
            }
        }
    }

    public string ApiSecret
    {
        get => _settings.Lastfm.ApiSecret ?? string.Empty;
        set
        {
            if (_settings.Lastfm.ApiSecret != value)
            {
                _settings.Lastfm.ApiSecret = value;
                OnPropertyChanged();
            }
        }
    }

    public bool Enabled
    {
        get => _settings.Lastfm.Enabled;
        set
        {
            if (_settings.Lastfm.Enabled != value)
            {
                ApplyEnabled(value);
                OnPropertyChanged();
            }
        }
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _authButtonText = string.Empty;
    public string AuthButtonText
    {
        get => _authButtonText;
        private set => SetProperty(ref _authButtonText, value);
    }

    private string _confirmButtonText = string.Empty;
    public string ConfirmButtonText
    {
        get => _confirmButtonText;
        private set => SetProperty(ref _confirmButtonText, value);
    }

    private bool _authButtonVisible;
    public bool AuthButtonVisible
    {
        get => _authButtonVisible;
        private set => SetProperty(ref _authButtonVisible, value);
    }

    private bool _confirmButtonVisible;
    public bool ConfirmButtonVisible
    {
        get => _confirmButtonVisible;
        private set => SetProperty(ref _confirmButtonVisible, value);
    }

    private bool _authButtonEnabled = true;
    public bool AuthButtonEnabled
    {
        get => _authButtonEnabled;
        private set => SetProperty(ref _authButtonEnabled, value);
    }

    private bool _confirmButtonEnabled = true;
    public bool ConfirmButtonEnabled
    {
        get => _confirmButtonEnabled;
        private set => SetProperty(ref _confirmButtonEnabled, value);
    }

    /// <summary>Re-reads the scrobbler state into the bindable surface. Call on section shown
    /// and whenever auth state may have changed.</summary>
    public void Refresh()
    {
        if (!_scrobbler.IsConfigured)
        {
            StatusText = AppStrings.Get("Lastfm_StatusNeedKeys", "Last.fm API 키와 비밀을 입력하세요 (last.fm/api 에서 생성).");
            AuthButtonVisible = false;
            ConfirmButtonVisible = false;
            return;
        }

        if (_scrobbler.IsAuthenticated)
        {
            StatusText = AppStrings.Format(
                "Lastfm_StatusAuthed", "{0} 로 인증됨 · 대기 중인 스러블 {1}개",
                string.IsNullOrEmpty(_scrobbler.Username) ? "?" : _scrobbler.Username, _scrobbler.QueuedCount);
            AuthButtonText = AppStrings.Get("Lastfm_Reauth", "다시 인증");
            AuthButtonVisible = true;
            ConfirmButtonVisible = false;
            return;
        }

        StatusText = AppStrings.Get("Lastfm_StatusNotAuthed", "브라우저에서 인증하면 스러블이 켜집니다.");
        AuthButtonText = AppStrings.Get("Lastfm_StartAuth", "브라우저에서 인증");
        AuthButtonVisible = true;
        ConfirmButtonVisible = false;
    }

    /// <summary>Persists the credential boxes into the scrobbler (and settings). Bound to the
    /// text boxes' LostFocus so half-typed keys are not applied per keystroke.</summary>
    public void ApplyCredentials()
    {
        _scrobbler.SetCredentials(ApiKey, ApiSecret);
        Refresh();
    }

    public void ApplyEnabled(bool enabled)
    {
        _scrobbler.SetCredentials(ApiKey, ApiSecret);
        _scrobbler.SetEnabled(enabled);
        Refresh();
    }

    /// <summary>Starts the token → browser → confirm flow. The token is parked on the scrobbler
    /// service: if the user navigates away before approving, the confirm step still finds it.</summary>
    public async Task StartAuthAsync()
    {
        try
        {
            _scrobbler.SetCredentials(ApiKey, ApiSecret);
            AuthButtonEnabled = false;
            string token = await _scrobbler.StartAuthAsync().ConfigureAwait(true);
            _scrobbler.PendingToken = token;
            var psi = new System.Diagnostics.ProcessStartInfo(
                LastfmClient.BuildAuthPageUrl(_settings.Lastfm.ApiKey, token))
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            StatusText = AppStrings.Get("Lastfm_AuthPending", "브라우저에서 권한을 허용한 뒤 [인증 완료]를 누르세요.");
            ConfirmButtonText = AppStrings.Get("Lastfm_ConfirmAuth", "인증 완료");
            ConfirmButtonVisible = true;
        }
        catch (Exception ex)
        {
            StatusText = AppStrings.Format("Lastfm_AuthFailed", "인증 실패: {0}", ex.Message);
        }
        finally
        {
            AuthButtonEnabled = true;
        }
    }

    public async Task ConfirmAuthAsync()
    {
        string? token = _scrobbler.PendingToken;
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            ConfirmButtonEnabled = false;
            string username = await _scrobbler.CompleteAuthAsync(token).ConfigureAwait(true);
            _scrobbler.PendingToken = null;
            StatusText = AppStrings.Format(
                "Lastfm_AuthDone", "{0} 로 인증되었습니다. 스러블이 켜졌습니다.", username);
        }
        catch (Exception ex)
        {
            StatusText = AppStrings.Format("Lastfm_AuthFailed", "인증 실패: {0}", ex.Message);
        }
        finally
        {
            ConfirmButtonEnabled = true;
            Refresh();
        }
    }
}
