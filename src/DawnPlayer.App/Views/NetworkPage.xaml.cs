namespace DawnPlayer.App.Views;

/// <summary>
/// The Network page: remote sources as sections (radio favorites, DLNA browsing, YouTube
/// playback). L13(2026-10-04): the switcher is a data-source sidebar — the same skeleton as
/// Library/Playlist. Activation stays lazy and the cached page preserves the selection.
/// </summary>
public sealed partial class NetworkPage : Microsoft.UI.Xaml.Controls.Page
{
    private bool _pageInitialized;
    private bool _dlnaActive;
    private bool _youTubeActive;

    public NetworkPage()
    {
        InitializeComponent();
    }

    public void ActivatePage()
    {
        if (_pageInitialized) return;
        _pageInitialized = true;
        RadioSectionControl.Activate();
    }

    /// <summary>Shell-facing entry point: opens the radio section's add-station dialog.</summary>
    public System.Threading.Tasks.Task OpenRadioAddDialogAsync() => RadioSectionControl.OpenAddDialogAsync();

    // 첫 행의 IsSelected=True가 XAML 파싱 중에 SelectionChanged를 일으킨다. 이 시점엔 콘텐츠
    // 그리드(섹션 컨트롤)가 아직 파싱 전이라 null이고, XAML 기본 가시성이 이미 "라디오만 표시"
    // 라는 정답 상태이므로 조기 반환이 정확한 처리다. 사용자 전환은 파싱 후에만 일어난다.
    private void OnSourceSelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (SourceList is null || RadioSectionControl is null) return;
        switch (SourceList.SelectedIndex)
        {
            case 1: ShowDlna(); break;
            case 2: ShowYouTube(); break;
            default: ShowRadio(); break;
        }
    }

    private void ShowRadio()
    {
        _dlnaActive = false;
        RadioSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        DlnaSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        YouTubeSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void ShowDlna()
    {
        if (!_dlnaActive)
        {
            _dlnaActive = true;
            DlnaSectionControl.Activate();
        }
        RadioSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        DlnaSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        YouTubeSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void ShowYouTube()
    {
        if (!_youTubeActive)
        {
            _youTubeActive = true;
            YouTubeSectionControl.Activate();
        }
        RadioSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        DlnaSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        YouTubeSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }
}
