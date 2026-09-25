using System.Collections.Generic;
using DawnPlayer.Core.Audio;
using DawnPlayer.Core.Models;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// The relay's contract: titles reach the raise callback with the item and station attached, and a
/// detached relay stays silent forever after (the track-left unsubscribe path depends on this).
/// </summary>
public sealed class LiveMetadataRelayTests
{
    private sealed class FakeLiveSource : ILiveMetadataSource
    {
        private readonly List<Action<string>> _handlers = [];

        public string StationName { get; set; } = "Test FM";

        public event Action<string>? StreamTitleChanged
        {
            add { if (value != null) _handlers.Add(value); }
            remove { _handlers.Remove(value!); }
        }

        public void Raise(string title)
        {
            foreach (var handler in _handlers.ToArray()) handler(title);
        }

        public int SubscriberCount => _handlers.Count;
    }

    private static PlaylistItem Item() =>
        new(RadioTrack.Create("http://radio.example/live") with { Title = "Station" });

    [Fact]
    public void Attach_RaisesWithItemAndStationName()
    {
        var source = new FakeLiveSource { StationName = "Jazz FM" };
        var item = Item();
        LiveStreamMetadata? received = null;

        LiveMetadataRelay.Attach(source, item, m => received = m);

        source.Raise("Artist - Song");

        Assert.NotNull(received);
        Assert.True(ReferenceEquals(item, received!.Value.Item));
        Assert.Equal("Jazz FM", received.Value.StationName);
        Assert.Equal("Artist - Song", received.Value.StreamTitle);
    }

    [Fact]
    public void Attach_ReadsStationNameAtRaiseTime_NotAtAttachTime()
    {
        // The station name arrives with the response headers, potentially after attach.
        var source = new FakeLiveSource { StationName = "" };
        LiveStreamMetadata? received = null;
        LiveMetadataRelay.Attach(source, Item(), m => received = m);

        source.StationName = "Late Name";
        source.Raise("Song");

        Assert.Equal("Late Name", received!.Value.StationName);
    }

    [Fact]
    public void Detach_StopsRelayingForever_AndRepeatedDetachIsHarmless()
    {
        var source = new FakeLiveSource();
        int raised = 0;
        var detach = LiveMetadataRelay.Attach(source, Item(), _ => raised++);

        detach();
        detach(); // idempotent

        source.Raise("one");
        source.Raise("two");

        Assert.Equal(0, raised);
        Assert.Equal(0, source.SubscriberCount);
    }

    [Fact]
    public void Detach_RemovesOnlyItsOwnSubscription()
    {
        var source = new FakeLiveSource();
        int other = 0;
        source.StreamTitleChanged += _ => other++;

        var detach = LiveMetadataRelay.Attach(source, Item(), _ => { });
        detach();

        source.Raise("x");

        Assert.Equal(1, other);
        Assert.Equal(1, source.SubscriberCount);
    }
}
