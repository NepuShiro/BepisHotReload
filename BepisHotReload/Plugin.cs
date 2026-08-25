using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using BepisModSettings.ConfigAttributes;
using BepisModSettings.DataFeeds;
using Elements.Core;
using HarmonyLib;
using Mono.Cecil;
using System.Reflection;
using System.Text;
using FrooxEngine;
using FrooxEngine.UIX;

namespace BepisHotReload;

// This is derived from BepInEx.Debug "ScriptEngine"
// https://github.com/BepInEx/BepInEx.Debug/blob/master/src/ScriptEngine/

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL), BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(BepisModSettings.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    private static ConfigEntry<bool> _loadOnStart = null!;
    private static ConfigEntry<bool> _quietMode = null!;
    private static ConfigEntry<bool> _includeSubdirectories = null!;
    private static ConfigEntry<bool> _includeNormalPlugins = null!;
    private static ConfigEntry<bool> _onlyPluginsWithUnload = null!;

    private static readonly string ReloadPath = Path.Combine(Paths.BepInExRootPath, "HotReload");

    private static readonly Dictionary<string, List<BasePlugin>> LoadedPluginsByFile = new Dictionary<string, List<BasePlugin>>(StringComparer.OrdinalIgnoreCase);

    public override void Load()
    {
        Log = base.Log;

        _loadOnStart = Config.Bind("General", "Load on Start", false, "Loads HotReload plugins whenever the game starts");
        _quietMode = Config.Bind("General", "QuietMode", false, "Disable all logging except for error messages.");
        _includeSubdirectories = Config.Bind("General", "IncludeSubdirectories", false, "Also load plugins from subdirectories of the reload folder.");
        _includeNormalPlugins = Config.Bind("General", "IncludeNormalPlugins", false, "Also include plugins from the normal BepInEx plugins folder.");
        _onlyPluginsWithUnload = Config.Bind("General", "OnlyPluginsWithUnload", false, "Only show and reload plugins that override Unload().");

        Config.SettingChanged += (_, args) =>
        {
            if (args.ChangedSetting is { } set && (set == _includeSubdirectories || set == _includeNormalPlugins || set == _onlyPluginsWithUnload))
            {
                DataFeedHelpers.RefreshSettingsScreen();
            }
        };

        Config.Bind("Plugins", "Loaded Plugins", default(dummy), new ConfigDescription("Individually reload any plugin DLL in the configured plugin folders.", null, new CustomDataFeed(EnumeratePlugins)));

        if (!Directory.Exists(ReloadPath))
            Directory.CreateDirectory(ReloadPath);

        if (_loadOnStart.Value)
            ReloadAll(true);

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");
    }

    public override bool Unload()
    {
        foreach (string file in LoadedPluginsByFile.Keys.ToList())
            UnloadFile(file);

        return true;
    }

    private static void UnloadFile(string path)
    {
        if (!LoadedPluginsByFile.TryGetValue(path, out List<BasePlugin>? plugins))
        {
            plugins = NetChainloader.Instance.Plugins.Values.Where(info => PathsEqual(info.Location, path)).Select(info => info.Instance).OfType<BasePlugin>().ToList();

            if (plugins.Count == 0) return;
        }

        if (!_quietMode.Value) Log.LogInfo($"Unloading plugins from {Path.GetFileName(path)}");

        foreach (BasePlugin plugin in plugins)
        {
            BepInPlugin metadata = MetadataHelper.GetMetadata(plugin);
            try
            {
                if (!plugin.Unload() && !_quietMode.Value)
                    Log.LogWarning($"{metadata.GUID} returned false from Unload() - it may not release everything it hooked");
            }
            catch (Exception e)
            {
                Log.LogError($"Exception unloading {metadata.GUID}: {e}");
            }

            NetChainloader.Instance.Plugins.Remove(metadata.GUID);
        }

        LoadedPluginsByFile.Remove(path);
    }

    private static void ReloadAll(bool load = false)
    {
        if (!_quietMode.Value) Log.LogInfo("Unloading old plugin instances");

        foreach (string file in LoadedPluginsByFile.Keys.ToList())
            UnloadFile(file);

        string[] files = GetReloadFiles(load);

        if (files.Length > 0)
        {
            foreach (string path in files)
                UnloadFile(path);

            foreach (string path in files)
                LoadDLL(path);

            if (!_quietMode.Value)
                Log.LogMessage("Reloaded all plugins!");
        }
        else if (!_quietMode.Value)
        {
            Log.LogMessage("No plugins to reload");
        }

        DataFeedHelpers.RefreshSettingsScreen();
    }

    private static void ReloadFile(string path)
    {
        if (!File.Exists(path))
        {
            if (!_quietMode.Value) Log.LogInfo($"{Path.GetFileName(path)} no longer exists - unloading only");
            UnloadFile(path);
            DataFeedHelpers.RefreshSettingsScreen();
            return;
        }

        UnloadFile(path);
        LoadDLL(path);

        if (!_quietMode.Value)
            Log.LogMessage($"Reloaded {Path.GetFileName(path)}");

        DataFeedHelpers.RefreshSettingsScreen();
    }

    private static void LoadDLL(string path)
    {
        DefaultAssemblyResolver defaultResolver = new DefaultAssemblyResolver();
        defaultResolver.AddSearchDirectory(ReloadPath);
        defaultResolver.AddSearchDirectory(Paths.GameRootPath);
        defaultResolver.AddSearchDirectory(Paths.BepInExAssemblyDirectory);
        AddPluginSearchDirectories(defaultResolver);

        if (!_quietMode.Value)
            Log.LogInfo($"Loading plugins from {path}");

        string symbolPath = Path.ChangeExtension(path, ".pdb");
        bool hasSymbols = File.Exists(symbolPath);

        using AssemblyDefinition dll = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
        {
            AssemblyResolver = defaultResolver,
            ReadSymbols = hasSymbols
        });

        dll.Name.Name = $"{dll.Name.Name}-{DateTime.Now.Ticks}";
        Assembly ass;
        if (!hasSymbols)
        {
            using MemoryStream ms = new MemoryStream();
            dll.Write(ms);
            ass = Assembly.Load(ms.ToArray());
        }
        else
        {
            string temporaryDirectory = Path.Combine(Path.GetTempPath(), "BepisHotReload", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);

            string temporaryAssemblyPath = Path.Combine(temporaryDirectory, Path.GetFileName(path));
            string temporarySymbolPath = Path.ChangeExtension(temporaryAssemblyPath, ".pdb");

            try
            {
                dll.Write(temporaryAssemblyPath, new WriterParameters { WriteSymbols = true });

                ass = Assembly.Load(File.ReadAllBytes(temporaryAssemblyPath), File.ReadAllBytes(temporarySymbolPath));
            }
            finally
            {
                try
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
                catch (Exception e)
                {
                    Log.LogDebug($"Failed to clean up temporary symbol files: {e.Message}");
                }
            }
        }

        foreach (Type? type in GetTypesSafe(ass))
        {
            try
            {
                if (!typeof(BasePlugin).IsAssignableFrom(type)) continue;
                if (_onlyPluginsWithUnload.Value && !HasUnloadOverride(type)) continue;

                BepInPlugin? metadata = MetadataHelper.GetMetadata(type);
                if (metadata == null) continue;

                if (!_quietMode.Value)
                    Log.LogInfo($"Loading {metadata.GUID}");

                if (NetChainloader.Instance.Plugins.ContainsKey(metadata.GUID))
                {
                    Log.LogError($"A plugin with GUID {metadata.GUID} is already loaded!");
                    continue;
                }

                TypeDefinition typeDefinition = dll.MainModule.Types.First(t => t.FullName == type.FullName);
                PluginInfo pluginInfo = NetChainloader.ToPluginInfo(typeDefinition, path);

                try
                {
                    BasePlugin instance = (BasePlugin)Activator.CreateInstance(type)!;

                    Traverse tv = Traverse.Create(pluginInfo);
                    tv.Property<BasePlugin>(nameof(pluginInfo.Instance)).Value = instance;
                    tv.Property<string>(nameof(pluginInfo.Location)).Value = path;

                    NetChainloader.Instance.Plugins[metadata.GUID] = pluginInfo;

                    if (!LoadedPluginsByFile.TryGetValue(path, out List<BasePlugin>? list))
                        LoadedPluginsByFile[path] = list = new List<BasePlugin>();
                    list.Add(instance);

                    instance.Load();
                }
                catch (Exception e)
                {
                    Log.LogError($"Failed to load plugin {metadata.GUID} because of exception: {e}");
                    NetChainloader.Instance.Plugins.Remove(metadata.GUID);
                }
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to load plugin {type?.Name} because of exception: {e}");
            }
        }
    }

    private static void AddPluginSearchDirectories(DefaultAssemblyResolver resolver)
    {
        AddDirectory(Paths.PluginPath);
        AddDirectory(Paths.PatcherPluginPath);
        return;

        void AddDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;

            resolver.AddSearchDirectory(directory);
            foreach (string subdirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                resolver.AddSearchDirectory(subdirectory);
        }
    }

    private static async IAsyncEnumerable<DataFeedItem> EnumeratePlugins(IReadOnlyList<string> path, IReadOnlyList<string> groupingKeys)
    {
        await Task.CompletedTask;

        DataFeedAction action = new DataFeedAction();
        action.InitBase("ReloadAll", path, groupingKeys, "Reload All", "Unloads and reloads every plugin in the configured plugin folders.");
        action.InitAction(syncDelegate =>
        {
            Button btn = syncDelegate.Slot.GetComponent<Button>();
            btn?.LocalPressed += (_, _) => ReloadAll();
        });
        yield return action;

        string[] files;
        try
        {
            files = GetReloadFiles();
        }
        catch (Exception e)
        {
            Log.LogError($"Failed to enumerate {ReloadPath}: {e}");
            yield break;
        }

        foreach (string file in files.OrderBy(f => f))
        {
            string fileName = Path.GetFileName(file);
            List<BasePlugin> plugins = GetLoadedPlugins(file);
            string status = plugins.Count > 0 ? $"Loaded: {string.Join(", ", plugins.Select(p => MetadataHelper.GetMetadata(p).GUID))}" : "Not loaded";

            DataFeedAction reload = new DataFeedAction();
            reload.InitBase("Reload_" + fileName, path, groupingKeys, $"Reload: {fileName}", status);
            reload.InitAction(syncDelegate =>
            {
                Button btn = syncDelegate.Slot.GetComponent<Button>();
                btn?.LocalPressed += (_, _) => ReloadFile(file);
            });
            yield return reload;
        }
    }

    private static string[] GetReloadFiles(bool load = false)
    {
        SearchOption searchOption = _includeSubdirectories.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        IEnumerable<string> files = Directory.Exists(ReloadPath) ? Directory.GetFiles(ReloadPath, "*.dll", searchOption) : Enumerable.Empty<string>();

        if (!load && _includeNormalPlugins.Value && Directory.Exists(Paths.PluginPath))
            files = files.Concat(Directory.GetFiles(Paths.PluginPath, "*.dll", searchOption));

        string currentAssemblyPath = NormalizePath(typeof(Plugin).Assembly.Location);
        return files.Select(NormalizePath)
            .Where(path => !PathsEqual(path, currentAssemblyPath))
            .Where(IsPluginAssembly)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path)
            .ToArray();
    }

    private static bool IsPluginAssembly(string path)
    {
        try
        {
            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
            resolver.AddSearchDirectory(Paths.GameRootPath);
            resolver.AddSearchDirectory(Paths.BepInExAssemblyDirectory);
            AddPluginSearchDirectories(resolver);

            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = false
            });

            return assembly.MainModule.Types.SelectMany(GetNestedTypes).Any(IsReloadablePluginType);
        }
        catch (Exception e)
        {
            if (!_quietMode.Value)
                Log.LogDebug($"Skipping {Path.GetFileName(path)} while checking for plugin types: {e.Message}");
            return false;
        }
    }

    private static IEnumerable<TypeDefinition> GetNestedTypes(TypeDefinition type)
    {
        yield return type;

        foreach (TypeDefinition nestedType in type.NestedTypes.SelectMany(GetNestedTypes))
            yield return nestedType;
    }

    private static bool IsPluginType(TypeDefinition type)
    {
        if (!type.IsClass || type.FullName == typeof(BasePlugin).FullName)
            return false;

        TypeReference? baseType = type.BaseType;
        while (baseType != null)
        {
            if (baseType.FullName == typeof(BasePlugin).FullName)
                return true;

            try
            {
                baseType = baseType.Resolve()?.BaseType;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsReloadablePluginType(TypeDefinition type) =>
        IsPluginType(type) && (!_onlyPluginsWithUnload.Value || HasUnloadOverride(type));

    private static bool HasUnloadOverride(Type type)
    {
        MethodInfo? unload = type.GetMethod(nameof(BasePlugin.Unload), BindingFlags.Instance | BindingFlags.Public);
        return unload != null && unload.GetBaseDefinition() != unload;
    }

    private static bool HasUnloadOverride(TypeDefinition type)
    {
        TypeReference? current = type;
        while (current != null)
        {
            TypeDefinition? definition = current.Resolve();
            if (definition == null || definition.FullName == typeof(BasePlugin).FullName)
                return false;

            if (definition.Methods.Any(method => method.Name == nameof(BasePlugin.Unload) && method.IsVirtual && !method.IsNewSlot &&
                                                 method.Parameters.Count == 0 && method.ReturnType.FullName == typeof(bool).FullName))
                return true;

            current = definition.BaseType;
        }

        return false;
    }

    private static List<BasePlugin> GetLoadedPlugins(string path)
    {
        if (LoadedPluginsByFile.TryGetValue(path, out List<BasePlugin>? plugins))
            return plugins;

        return NetChainloader.Instance.Plugins.Values.Where(info => PathsEqual(info.Location, path)).Select(info => info.Instance).OfType<BasePlugin>().ToList();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Type?> GetTypesSafe(Assembly ass)
    {
        try
        {
            return ass.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            StringBuilder sbMessage = new StringBuilder();
            sbMessage.AppendLine("\r\n-- LoaderExceptions --");
            foreach (Exception? l in ex.LoaderExceptions)
            {
                sbMessage.AppendLine(l?.ToString());
            }
            sbMessage.AppendLine("\r\n-- StackTrace --");
            sbMessage.AppendLine(ex.StackTrace);
            Log.LogError(sbMessage.ToString());
            return ex.Types.Where(x => x != null);
        }
    }
}
