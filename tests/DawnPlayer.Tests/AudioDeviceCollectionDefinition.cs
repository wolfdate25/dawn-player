using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Serializes test classes that open a real audio output device and start playback.
/// <para>
/// Several of these tests assert that the controller is still <c>Playing</c> after starting a track.
/// When they run concurrently they compete for the same endpoint, an output initialization
/// occasionally fails, and the controller correctly reports <c>Stopped</c> — so the test failed for
/// reasons that had nothing to do with the code under test. One device, one test at a time.
/// </para>
/// </summary>
[CollectionDefinition("AudioDeviceCollection", DisableParallelization = true)]
public class AudioDeviceCollectionDefinition
{
}
