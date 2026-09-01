using System.Reflection;
using System.Runtime.Loader;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using DawnPlayer.Plugins;

namespace DawnPlayer.Core.Lyrics.Online;

/// <summary>Everything the UI needs to know about one loaded plugin.</summary>
public sealed record LyricsPluginInfo(string Id, string Name, string Version, string Author, bool IsExternal);

/// <summary>A plugin instance plus its metadata.</summary>
public sealed record LoadedLyricsPlugin(LyricsPluginInfo Info, ILyricsPlugin Plugin);

/// <summary>
/// Discovers, loads and instantiates lyrics plugins. Each subfolder of the plugins directory is
/// one plugin: every .dll inside is loaded into a folder-scoped AssemblyLoadContext (so plugins
/// can ship private dependencies), and any type marked with <see cref="LyricsPluginAttribute"/>
/// implementing <see cref="ILyricsPlugin"/> is instantiated. A broken plugin is logged and
/// skipped; it never prevents other plugins or the app from loading.
/// </summary>
/// <remarks>
/// Contexts are not collectible: dropping a DLL into the folder and pressing "다시 스캔" picks up
/// new plugins, but replacing an existing one in place requires an app restart. Unloading
/// assemblies while instances are still referenced by the search pipeline is not worth the hazard.
/// </remarks>
public sealed class LyricsPluginHost
{
    private readonly object _lock = new();
    private readonly List<LoadedLyricsPlugin> _plugins = new();
    private readonly List<AssemblyLoadContext> _contexts = new();
    private readonly List<string> _loadErrors = new();
    private readonly Func<AppSettings> _settings;
    private readonly Action<string>? _log;

    /// <summary>Raised (from any thread) after the plugin set changed via <see cref="Reload"/> or <see cref="RegisterInstance"/>.</summary>
    public event Action? PluginsChanged;

    public LyricsPluginHost(Func<AppSettings> settings, Action<string>? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log;
    }

    public IReadOnlyList<LoadedLyricsPlugin> Plugins
    {
        get { lock (_lock) return _plugins.ToList(); }
    }

    public IReadOnlyList<string> LoadErrors
    {
        get { lock (_lock) return _loadErrors.ToList(); }
    }

    /// <summary>Scans the plugins directory. Previously loaded external plugins are dropped first.</summary>
    public void Reload()
    {
        lock (_lock)
        {
            _plugins.RemoveAll(p => p.Info.IsExternal);
            _loadErrors.Clear();

            try
            {
                if (Directory.Exists(AppPaths.PluginsDir))
                {
                    foreach (var folder in Directory.EnumerateDirectories(AppPaths.PluginsDir))
                        LoadFolderLocked(folder);
                }
            }
            catch (Exception ex)
            {
                _loadErrors.Add($"플러그인 폴더 스캔 실패: {ex.Message}");
            }
        }

        PluginsChanged?.Invoke();
    }

    /// <summary>Loads one folder of DLLs into its own assembly context and collects plugin types.</summary>
    private void LoadFolderLocked(string folder)
    {
        string[] dlls;
        try
        {
            dlls = Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex)
        {
            _loadErrors.Add($"{Path.GetFileName(folder)}: {ex.Message}");
            return;
        }
        if (dlls.Length == 0) return;

        var context = new AssemblyLoadContext($"DawnLyricsPlugin:{Path.GetFileName(folder)}", isCollectible: false);
        _contexts.Add(context);
        var found = 0;

        foreach (var dll in dlls)
        {
            try
            {
                var assembly = context.LoadFromAssemblyPath(dll);
                found += LoadPluginsFromAssemblyLocked(assembly, isExternal: true);
            }
            catch (ReflectionTypeLoadException ex)
            {
                _loadErrors.Add($"{Path.GetFileName(dll)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Dependency-only DLLs commonly fail to load standalone; only worth reporting
                // when the folder produced no plugins at all.
                _loadErrors.Add($"{Path.GetFileName(dll)}: {ex.Message}");
            }
        }

        if (found == 0 && _loadErrors.Count > 0)
        {
            Log($"플러그인 폴더 로드 실패: {folder}");
        }
    }

    /// <summary>Scans an already-loaded assembly (tests, in-box providers) for plugin types.</summary>
    public int LoadFromAssembly(Assembly assembly)
    {
        lock (_lock)
        {
            return LoadPluginsFromAssemblyLocked(assembly, isExternal: false);
        }
    }

    /// <summary>Registers a pre-built instance (tests). The instance must carry <see cref="LyricsPluginAttribute"/>.</summary>
    public void RegisterInstance(ILyricsPlugin plugin)
    {
        var attr = plugin.GetType().GetCustomAttribute<LyricsPluginAttribute>()
            ?? throw new InvalidOperationException($"{plugin.GetType().Name}에 LyricsPluginAttribute가 없습니다.");

        lock (_lock)
        {
            var info = new LyricsPluginInfo(attr.Id, attr.Name, attr.Version, attr.Author, IsExternal: false);
            AddPluginLocked(info, plugin);
        }

        PluginsChanged?.Invoke();
    }

    private int LoadPluginsFromAssemblyLocked(Assembly assembly, bool isExternal)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

        var found = 0;
        foreach (var type in types)
        {
            LyricsPluginAttribute? attr;
            try { attr = type.GetCustomAttribute<LyricsPluginAttribute>(); }
            catch { continue; }

            if (attr is null || !typeof(ILyricsPlugin).IsAssignableFrom(type) || type.IsAbstract) continue;

            try
            {
                var plugin = Instantiate(type, attr.Id);
                AddPluginLocked(new LyricsPluginInfo(attr.Id, attr.Name, attr.Version, attr.Author, isExternal), plugin);
                found++;
            }
            catch (Exception ex)
            {
                _loadErrors.Add($"{attr.Id}: 인스턴스 생성 실패 - {ex.Message}");
            }
        }
        return found;
    }

    private ILyricsPlugin Instantiate(Type type, string pluginId)
    {
        var ctorWithCtx = type.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(ILyricsPluginContext));
        if (ctorWithCtx != null)
            return (ILyricsPlugin)ctorWithCtx.Invoke(new object?[] { CreateContext(pluginId) });

        var ctor = type.GetConstructor(Array.Empty<Type>())
            ?? throw new InvalidOperationException("public parameterless 또는 ILyricsPluginContext 생성자가 필요합니다.");
        return (ILyricsPlugin)ctor.Invoke(Array.Empty<object?>());
    }

    private void AddPluginLocked(LyricsPluginInfo info, ILyricsPlugin plugin)
    {
        if (_plugins.Any(p => p.Info.Id.Equals(info.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _loadErrors.Add($"{info.Id}: 중복된 플러그인 id (이미 등록됨)");
            return;
        }
        _plugins.Add(new LoadedLyricsPlugin(info, plugin));
        Log($"가사 플러그인 로드: {info.Name} v{info.Version} ({info.Id}){(info.IsExternal ? "" : ", 내장 등록")}");
    }

    private PluginContext CreateContext(string pluginId) => new(pluginId, _settings, _log);

    private void Log(string message) => _log?.Invoke($"[lyrics-plugin] {message}");

    private sealed class PluginContext : ILyricsPluginContext
    {
        private readonly string _pluginId;
        private readonly Func<AppSettings> _settings;
        private readonly Action<string>? _log;

        public PluginContext(string pluginId, Func<AppSettings> settings, Action<string>? log)
        {
            _pluginId = pluginId;
            _settings = settings;
            _log = log;
            try
            {
                DataFolder = Path.Combine(AppPaths.PluginsDataDir, pluginId);
                Directory.CreateDirectory(DataFolder);
            }
            catch
            {
                DataFolder = Path.GetTempPath();
            }
        }

        public string DataFolder { get; }

        public string? GetSetting(string key)
        {
            try
            {
                var options = _settings().LyricsOnline.PluginOptions;
                return options.TryGetValue(_pluginId, out var bag) && bag.TryGetValue(key, out var value)
                    ? value
                    : null;
            }
            catch
            {
                return null;
            }
        }

        public void Log(string message) => _log?.Invoke($"[plugin:{_pluginId}] {message}");
    }
}
