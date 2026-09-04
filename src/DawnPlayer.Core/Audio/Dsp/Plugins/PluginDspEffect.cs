using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using DawnPlayer.Core.Persistence;
using DawnPlayer.Core.Util;
using DawnPlayer.Plugins;

namespace DawnPlayer.Core.Audio.Dsp.Plugins;

/// <summary>
/// Chain adapter that runs every loaded <see cref="IDspPlugin"/>'s effect, in load order, after
/// the built-in effects. Effect instances are (re)created lazily on the render thread the first
/// time they are needed after an enable or format change — creation is O(plugins) and happens
/// once per session, not per block.
/// </summary>
public sealed class PluginDspEffect : IAudioDspEffect
{
    string IAudioDspEffect.Name => "PluginDsp";

    public bool IsEnabled { get; set; }

    private readonly DspPluginLoader? _host;
    private readonly Func<DspPluginLoader?>? _hostProvider;

    private IDspEffectInstance[]? _instances;
    private int _sampleRate;
    private int _channels;
    private bool _instancesForCurrentFormat;

    public PluginDspEffect(DspPluginLoader host)
    {
        _host = host;
    }

    /// <summary>Late-binding constructor: resolves the loader on first use; null disables plugins.</summary>
    public PluginDspEffect(Func<DspPluginLoader?> hostProvider)
    {
        _hostProvider = hostProvider;
    }

    private DspPluginLoader? ResolveHost() => _host ?? _hostProvider?.Invoke();

    public void Initialize(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        // Format changes invalidate the instances' internal state.
        _instancesForCurrentFormat = false;
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (!IsEnabled || buffer == null || count <= 0) return;
        if (!EnsureInstances()) return;

        var instances = _instances!;
        foreach (var instance in instances)
        {
            try
            {
                instance.Process(buffer, offset, count);
            }
            catch
            {
                // A throwing plugin must not kill the render thread: drop it for this session.
                _instances = Array.Empty<IDspEffectInstance>();
                return;
            }
        }
    }

    public void Reset()
    {
        var instances = _instances;
        if (instances == null) return;
        foreach (var instance in instances)
        {
            try { instance.Reset(); } catch { }
        }
    }

    private bool EnsureInstances()
    {
        if (_instancesForCurrentFormat && _instances != null) return _instances.Length > 0;
        if (!_instancesForCurrentFormat)
        {
            var host = ResolveHost();
            if (host == null) return false; // no loader attached yet (fine to retry later)
            _instances = host.Plugins.Select(p => p.Plugin.CreateEffect()).ToArray();
            foreach (var instance in _instances)
            {
                try { instance.Initialize(_sampleRate, _channels); }
                catch
                {
                    _instances = Array.Empty<IDspEffectInstance>();
                    _instancesForCurrentFormat = true;
                    return false;
                }
            }
            _instancesForCurrentFormat = true;
        }
        return _instances!.Length > 0;
    }
}

/// <summary>Everything the UI needs to know about one loaded DSP plugin.</summary>
public sealed record DspPluginInfo(string Id, string Name, string Version, string Author, bool IsExternal);

/// <summary>A DSP plugin plus its metadata.</summary>
public sealed record LoadedDspPlugin(DspPluginInfo Info, IDspPlugin Plugin);

/// <summary>
/// Discovers, loads and instantiates DSP effect plugins. The loading model mirrors the lyrics
/// plugin host: each subfolder of the plugins directory is one plugin loaded into a
/// folder-scoped AssemblyLoadContext, and types marked with <see cref="DspPluginAttribute"/>
/// implementing <see cref="IDspPlugin"/> are instantiated. A broken plugin is logged and skipped.
/// </summary>
public sealed class DspPluginLoader
{
    private readonly object _lock = new();
    private readonly List<LoadedDspPlugin> _plugins = new();
    private readonly List<string> _loadErrors = new();
    private readonly Action<string>? _log;

    public DspPluginLoader(Action<string>? log = null) => _log = log;

    public IReadOnlyList<LoadedDspPlugin> Plugins
    {
        get { lock (_lock) return _plugins.ToList(); }
    }

    public IReadOnlyList<string> LoadErrors
    {
        get { lock (_lock) return _loadErrors.ToList(); }
    }

    /// <summary>Scans the plugins directory, replacing all externally loaded plugins.</summary>
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
                _loadErrors.Add($"plugin folder scan failed: {ex.Message}");
            }
        }
    }

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

        var context = new AssemblyLoadContext($"DawnDspPlugin:{Path.GetFileName(folder)}", isCollectible: false);

        foreach (var dll in dlls)
        {
            try
            {
                var assembly = context.LoadFromAssemblyPath(dll);
                LoadFromAssemblyLocked(assembly, isExternal: true);
            }
            catch (Exception ex)
            {
                // Dependency-only DLLs commonly fail to load standalone; logged and skipped.
                _loadErrors.Add($"{Path.GetFileName(dll)}: {ex.Message}");
            }
        }
    }

    private void LoadFromAssemblyLocked(Assembly assembly, bool isExternal)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

        foreach (var type in types)
        {
            DspPluginAttribute? attr;
            try { attr = type.GetCustomAttribute<DspPluginAttribute>(); }
            catch { continue; }

            if (attr is null || !typeof(IDspPlugin).IsAssignableFrom(type) || type.IsAbstract) continue;

            try
            {
                if (type.GetConstructor(Array.Empty<Type>()) is not { } ctor)
                {
                    throw new InvalidOperationException("public parameterless constructor required.");
                }
                var plugin = (IDspPlugin)ctor.Invoke(Array.Empty<object?>());
                AddPluginLocked(new DspPluginInfo(attr.Id, attr.Name, attr.Version, attr.Author, isExternal), plugin);
            }
            catch (Exception ex)
            {
                _loadErrors.Add($"{attr.Id}: instantiation failed - {ex.Message}");
            }
        }
    }

    /// <summary>Scans an already-loaded assembly (tests, in-box providers) for DSP plugin types.</summary>
    public int LoadFromAssembly(Assembly assembly)
    {
        lock (_lock)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }

            var found = 0;
            foreach (var type in types)
            {
                DspPluginAttribute? attr;
                try { attr = type.GetCustomAttribute<DspPluginAttribute>(); }
                catch { continue; }

                if (attr is null || !typeof(IDspPlugin).IsAssignableFrom(type) || type.IsAbstract) continue;

                try
                {
                    if (type.GetConstructor(Array.Empty<Type>()) is not { } ctor)
                    {
                        throw new InvalidOperationException("public parameterless constructor required.");
                    }
                    var plugin = (IDspPlugin)ctor.Invoke(Array.Empty<object?>());
                    AddPluginLocked(new DspPluginInfo(attr.Id, attr.Name, attr.Version, attr.Author, IsExternal: false), plugin);
                    found++;
                }
                catch (Exception ex)
                {
                    _loadErrors.Add($"{attr.Id}: instantiation failed - {ex.Message}");
                }
            }
            return found;
        }
    }

    private void AddPluginLocked(DspPluginInfo info, IDspPlugin plugin)
    {
        if (_plugins.Any(p => p.Info.Id.Equals(info.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _loadErrors.Add($"{info.Id}: duplicate plugin id (already registered)");
            return;
        }
        _plugins.Add(new LoadedDspPlugin(info, plugin));
        _log?.Invoke($"[dsp-plugin] loaded: {info.Name} v{info.Version} ({info.Id})");
    }
}
