using DawnPlayer.Core.Models;
using DawnPlayer.Core.Persistence;

namespace DawnPlayer.App.Services;

/// <summary>
/// Scrobble orchestration: now-playing updates on track start, scrobbles on counted plays
/// (the same heuristic the listening statistics use), an in-memory retry queue for network
/// failures, and the interactive web-auth flow. All Last.fm I/O happens on the thread pool; the
/// service is called from the UI thread and never blocks it.
/// </summary>
public sealed class ScrobbleService : IDisposable
{
    public void Dispose() => _client.Dispose();
    /// <summary>Scrobbles are only valid within 2 weeks of playback.</summary>
    private static readonly TimeSpan MaxQueueAge = TimeSpan.FromDays(13);
    private const int MaxQueueSize = 50;

    private readonly Func<AppSettings> _settings;
    private readonly LastfmClient _client;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly Queue<Scrobble> _queue = new();

    /// <summary>Raised (on the UI thread via the caller) when auth state or queue size changes
    /// and the settings dialog should refresh.</summary>
    public event Action? StateChanged;

    private long _currentStartedUtc;

    public ScrobbleService(Func<AppSettings> settings, Action<string> log)
    {
        _settings = settings;
        _client = new LastfmClient(() => _settings().Lastfm);
        _log = log;
    }

    public bool Enabled => _settings().Lastfm.Enabled;
    public bool IsAuthenticated => _client.IsAuthenticated;
    public bool IsConfigured => _client.IsConfigured;
    public string Username => _settings().Lastfm.Username;
    public int QueuedCount { get { lock (_lock) return _queue.Count; } }

    /// <summary>The token issued by the in-flight auth flow. Held on the app-scoped service
    /// (not on the settings page) so navigating away mid-flow does not orphan a browser
    /// approval the user already granted.</summary>
    public string? PendingToken { get; set; }

    /// <summary>Starts the auth flow: returns the browser URL to open. The user then clicks
    /// "confirm" in the dialog, which calls <see cref="CompleteAuthAsync"/> with the same token.</summary>
    public async Task<string> StartAuthAsync()
    {
        var settings = _settings().Lastfm;
        string token = await _client.GetTokenAsync().ConfigureAwait(false);
        settings.SessionKey = ""; // a fresh flow invalidates any stale session
        SettingsWriter.Schedule(_settings());
        return LastfmClient.BuildAuthPageUrl(settings.ApiKey, token);
    }

    /// <summary>Exchanges a browser-approved token for a session, persisting it. The token is the
    /// one <see cref="StartAuthAsync"/> returned (the service re-derives it from the URL the host
    /// passes back in — the host keeps hold of it instead).</summary>
    public async Task<string> CompleteAuthAsync(string token)
    {
        var (key, username) = await _client.GetSessionAsync(token).ConfigureAwait(false);
        var settings = _settings().Lastfm;
        settings.SessionKey = key;
        settings.Username = username;
        settings.Enabled = true;
        SettingsWriter.Schedule(_settings());
        StateChanged?.Invoke();
        return username;
    }

    public void SetEnabled(bool enabled)
    {
        _settings().Lastfm.Enabled = enabled;
        SettingsWriter.Schedule(_settings());
        StateChanged?.Invoke();
    }

    public void SetCredentials(string apiKey, string apiSecret)
    {
        var settings = _settings().Lastfm;
        // Changing credentials invalidates the session signed with the old secret.
        if (settings.ApiKey != apiKey || settings.ApiSecret != apiSecret)
        {
            settings.SessionKey = "";
            settings.Username = "";
        }
        settings.ApiKey = apiKey.Trim();
        settings.ApiSecret = apiSecret.Trim();
        SettingsWriter.Schedule(_settings());
        StateChanged?.Invoke();
    }

    /// <summary>Now-playing update when a track starts (radio streams excluded — no tags).</summary>
    public void NotifyTrackStarted(Track track)
    {
        if (track == null || !Enabled || !IsAuthenticated) return;
        if (Core.Audio.RadioTrack.IsStreamUrl(track.Path)) return;
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title)) return;

        Volatile.Write(ref _currentStartedUtc, DateTime.UtcNow.Ticks);
        int duration = (int)Math.Round(track.Duration.TotalSeconds);
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.UpdateNowPlayingAsync(track.Artist, track.Title, track.Album, duration).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"[lastfm] now-playing failed: {ex.Message}");
            }
        });
    }

    /// <summary>Scrobbles a counted play. Failures queue for retry (oldest-first) on the next
    /// successful call; stale (>13 days) and over-cap entries are dropped per Last.fm guidance.</summary>
    public void NotifyTrackPlayed(Track track, TimeSpan startedAgo)
    {
        if (track == null || !Enabled || !IsAuthenticated) return;
        if (Core.Audio.RadioTrack.IsStreamUrl(track.Path)) return;
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title)) return;

        if (startedAgo <= TimeSpan.Zero)
        {
            // No explicit start time given: derive from when the service saw the track start.
            startedAgo = DateTime.UtcNow - new DateTime(Volatile.Read(ref _currentStartedUtc), DateTimeKind.Utc);
        }
        startedAgo = TimeSpan.FromTicks(Math.Clamp(startedAgo.Ticks, TimeSpan.Zero.Ticks, track.Duration.Ticks > 0 ? track.Duration.Ticks : startedAgo.Ticks));
        long startUnix = DateTimeOffset.UtcNow.Subtract(startedAgo).ToUnixTimeSeconds();
        var scrobble = new Scrobble(
            track.Artist, track.Title, track.Album,
            (int)Math.Round(track.Duration.TotalSeconds), startUnix, track.Path);

        lock (_lock)
        {
            TrimQueueLocked();
            _queue.Enqueue(scrobble);
        }
        FlushAsync();
    }

    /// <summary>Retries whatever is queued (also usable on startup or settings changes).</summary>
    public void FlushAsync()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Scrobble[] batch;
                lock (_lock)
                {
                    if (_queue.Count == 0) return;
                    batch = _queue.ToArray();
                }

                try
                {
                    bool ok = await _client.ScrobbleAsync(batch).ConfigureAwait(false);
                    if (!ok) return;
                    lock (_lock)
                    {
                        for (int i = 0; i < batch.Length && _queue.Count > 0; i++) _queue.Dequeue();
                    }
                    StateChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    _log($"[lastfm] scrobble deferred ({QueuedCount} queued): {ex.Message}");
                    return; // retried on the next track's scrobble
                }
            }
        });
    }

    private void TrimQueueLocked()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(MaxQueueAge).ToUnixTimeSeconds();
        while (_queue.Count > 0 &&
               (_queue.Count >= MaxQueueSize || _queue.Peek().StartUnix < cutoff))
        {
            _queue.Dequeue();
        }
    }
}
