namespace DawnPlayer.App.Views;

/// <summary>
/// The Network tab: remote sources as sections (radio favorites, DLNA browsing, YouTube playback).
/// Activation is lazy like the other content pages.
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

    private void OnSectionRadioClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _dlnaActive = false;
        RadioSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        DlnaSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        YouTubeSectionControl.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void OnSectionDlnaClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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

    private void OnSectionYouTubeClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
