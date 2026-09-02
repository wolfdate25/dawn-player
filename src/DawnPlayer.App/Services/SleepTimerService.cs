using DawnPlayer.App.Localization;

namespace DawnPlayer.App.Services;

/// <summary>The user-facing sleep timer choices.</summary>
public enum SleepTimerOption
{
    Off,
    Minutes15,
    Minutes30,
    Minutes60,
    AfterCurrentTrack
}

/// <summary>
/// Pauses playback after a countdown, or right after the current track ends. The countdown runs
/// on a thread timer and marshals the expiry to the UI thread; "after current track" reuses
/// <see cref="Core.Audio.PlaybackController.StopAfterCurrent"/>, which already stops exactly at
/// the boundary, and resets itself when that stop lands.
/// </summary>
public sealed class SleepTimerService : IDisposable
{
    private readonly object _gate = new();
    private System.Threading.Timer? _countdown;
    private long _deadlineTick64;   // Environment.TickCount64 ms; 0 = no countdown running
    private SleepTimerOption _option = SleepTimerOption.Off;

    public SleepTimerOption Active => _option;

    /// <summary>Raised on the UI thread whenever the active option changes.</summary>
    public event Action? Changed;

    /// <summary>Localized menu label for the active option, with remaining time when counting down.</summary>
    public string DescribeActive()
    {
        switch (_option)
        {
            case SleepTimerOption.Minutes15:
            case SleepTimerOption.Minutes30:
            case SleepTimerOption.Minutes60:
                var total = _option switch
                {
                    SleepTimerOption.Minutes15 => 15,
                    SleepTimerOption.Minutes30 => 30,
                    _ => 60
                };
                var label = AppStrings.Get($"MainWindow_Menu_Sleep_{total}", total == 60 ? "1시간" : $"{total}분");
                var remaining = TimeSpan.FromMilliseconds(Math.Max(0, Volatile.Read(ref _deadlineTick64) - Environment.TickCount64));
                return AppStrings.Format("MainWindow_Menu_Sleep_RemainingFormat", "{0} ({1:mm\\:ss})", label, remaining);

            case SleepTimerOption.AfterCurrentTrack:
                return AppStrings.Get("MainWindow_Menu_Sleep_Track", "현재 곡 끝나고");

            default:
                return AppStrings.Get("MainWindow_Menu_Sleep_Off", "끔");
        }
    }

    public void Set(SleepTimerOption option)
    {
        lock (_gate)
        {
            _countdown?.Dispose();
            _countdown = null;
            _deadlineTick64 = 0;
            _option = option;

            var delay = option switch
            {
                SleepTimerOption.Minutes15 => TimeSpan.FromMinutes(15),
                SleepTimerOption.Minutes30 => TimeSpan.FromMinutes(30),
                SleepTimerOption.Minutes60 => TimeSpan.FromMinutes(60),
                _ => Timeout.InfiniteTimeSpan
            };

            switch (option)
            {
                case SleepTimerOption.Off:
                    // Clearing the timer must also clear a borrowed stop-after-current flag, or the
                    // user would wonder why playback still stops at the end of the current track.
                    AppServices.Playback.StopAfterCurrent = false;
                    break;

                case SleepTimerOption.AfterCurrentTrack:
                    // The controller implements exactly this boundary; OnStopAfterCurrentConsumed
                    // resets the menu state when the stop lands.
                    AppServices.Playback.StopAfterCurrent = true;
                    break;

                default:
                    Volatile.Write(ref _deadlineTick64, Environment.TickCount64 + (long)delay.TotalMilliseconds);
                    _countdown = new System.Threading.Timer(_ => Expire(), null, delay, Timeout.InfiniteTimeSpan);
                    break;
            }
        }

        RaiseChanged();
    }

    private void Expire()
    {
        lock (_gate)
        {
            _countdown?.Dispose();
            _countdown = null;
            _deadlineTick64 = 0;
            _option = SleepTimerOption.Off;
        }

        AppServices.RunOnUi(() =>
        {
            if (AppServices.Playback.State == Core.Audio.PlaybackState.Playing)
            {
                AppServices.Playback.PlayPause();
            }
            Changed?.Invoke();
        });
    }

    /// <summary>Called on the UI thread when a stop-after-current stop has landed.</summary>
    internal void OnStopAfterCurrentConsumed()
    {
        lock (_gate)
        {
            if (_option != SleepTimerOption.AfterCurrentTrack) return;
            _option = SleepTimerOption.Off;
        }
        RaiseChanged();
    }

    private void RaiseChanged() => AppServices.RunOnUi(() => Changed?.Invoke());

    public void Dispose()
    {
        lock (_gate)
        {
            _countdown?.Dispose();
            _countdown = null;
        }
    }
}
