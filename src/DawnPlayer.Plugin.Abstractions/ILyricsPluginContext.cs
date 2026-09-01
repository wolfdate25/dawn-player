namespace DawnPlayer.Plugins;

/// <summary>
/// Host services handed to a plugin. Declare a single constructor parameter of this type to
/// receive it; a parameterless constructor also works.
/// </summary>
public interface ILyricsPluginContext
{
    /// <summary>Per-plugin data folder (created by the host). Safe for arbitrary cached files.</summary>
    string DataFolder { get; }

    /// <summary>A user-configured plugin setting (from 설정 → 온라인 가사), or null when unset.</summary>
    string? GetSetting(string key);

    /// <summary>Appends a line to the player log. Never throws.</summary>
    void Log(string message);
}
