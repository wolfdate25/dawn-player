using Xunit;

namespace DawnPlayer.Tests;

/// <summary>
/// Serializes test classes that read or write the shared application data directory — settings.json,
/// the art cache, the playlists folder — or that mutate process-wide statics such as
/// <c>AppPaths</c>'s base directory. These cannot run beside each other, or beside tests that assert
/// on the same files, without interfering.
/// <para>
/// DisableParallelization was previously false, which meant this collection served no purpose: the
/// classes in it still ran concurrently with everything else and the suite failed intermittently in
/// a different place on almost every run.
/// </para>
/// </summary>
[CollectionDefinition("SettingsStoreCollection", DisableParallelization = true)]
public class SettingsStoreCollectionDefinition
{
}
