using DawnPlayer.Core.Audio;
using Xunit;

namespace DawnPlayer.Tests.Audio;

/// <summary>
/// The shared PCM queue's generation gate: a superseded producer (a seek restarted the chain)
/// can neither add bytes nor latch end-of-stream into the new producer's buffer — the check and
/// the effect happen under one lock, so there is no check-act window. Dead (radio's permanent
/// "source lost") always wins over later writes.
/// </summary>
public sealed class BufferedPcmTests
{
    [Fact]
    public void StaleGeneration_AddBytes_AreDropped()
    {
        var pcm = new BufferedPcm();
        pcm.Reset(generation: 1);

        Assert.True(pcm.AddBytes(new byte[10], 10, generation: 1));
        pcm.Reset(generation: 2); // a seek restarted the producer

        Assert.False(pcm.AddBytes(new byte[10], 10, generation: 1)); // old chain's late write
        Assert.Equal(0, pcm.BufferedBytes); // nothing from the stale chain survived
        Assert.True(pcm.AddBytes(new byte[10], 10, generation: 2));
        Assert.Equal(10, pcm.BufferedBytes);
    }

    [Fact]
    public void StaleGeneration_CannotEndTheNewProducersTrack()
    {
        var pcm = new BufferedPcm();
        pcm.Reset(generation: 1);
        pcm.Reset(generation: 2);

        pcm.MarkEnded(generation: 1); // the superseded chain's EOF must not end the new track

        Assert.False(pcm.HasEnded);
        pcm.MarkEnded(generation: 2);
        Assert.True(pcm.HasEnded);
    }

    [Fact]
    public void Reset_ClearsTheEndedLatch_AndDrainsBufferedBytes()
    {
        var pcm = new BufferedPcm();
        pcm.Reset(1);
        pcm.AddBytes(new byte[10], 10, 1);
        pcm.MarkEnded(1);
        Assert.True(pcm.HasEnded);

        pcm.Reset(2);

        Assert.False(pcm.HasEnded);
        Assert.Equal(0, pcm.BufferedBytes);
    }

    [Fact]
    public void Dead_RejectsLaterWrites_AndWinsOverReset()
    {
        var pcm = new BufferedPcm();
        pcm.Reset(1);
        pcm.MarkDead();

        Assert.False(pcm.AddBytes(new byte[10], 10, 1));
        Assert.Equal(0, pcm.BufferedBytes);
    }
}
