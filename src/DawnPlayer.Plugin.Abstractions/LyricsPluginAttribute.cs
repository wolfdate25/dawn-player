namespace DawnPlayer.Plugins;

/// <summary>
/// Marks a class as a Dawn Player lyrics plugin and carries the metadata the host shows in
/// the plugin list. Apply it to exactly one class implementing <see cref="ILyricsPlugin"/>.
/// </summary>
/// <remarks>
/// The host instantiates the attributed class when scanning the plugin assembly. The class must
/// have a public parameterless constructor, or a single constructor taking
/// <see cref="ILyricsPluginContext"/> (recommended when the plugin needs settings or logging).
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class LyricsPluginAttribute : Attribute
{
    /// <summary>Stable unique identifier, e.g. "lrclib". Persisted in settings; never change it after release.</summary>
    public string Id { get; }

    /// <summary>Display name shown in the UI, e.g. "LRCLIB".</summary>
    public string Name { get; }

    /// <summary>Plugin version, e.g. "1.0.0".</summary>
    public string Version { get; }

    /// <summary>Plugin author shown in the UI.</summary>
    public string Author { get; }

    public LyricsPluginAttribute(string id, string name, string version, string author)
    {
        Id = id;
        Name = name;
        Version = version;
        Author = author;
    }
}
