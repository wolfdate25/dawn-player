using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// xUnit collection definition to synchronize playlist and playback queue concurrency test classes.
/// Disables parallel execution across tests belonging to this collection to prevent threadpool starvation
/// and file I/O contention during full test runs.
/// </summary>
[CollectionDefinition("PlaylistConcurrencyCollection", DisableParallelization = true)]
public class PlaylistConcurrencyCollectionDefinition
{
}
