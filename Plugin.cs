using System.Collections.Generic;
using System.IO;
using System.Timers;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NetLauncher.Common;
using HarmonyLib;

namespace SaS2ModOptions;

[BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginName, PluginInfo.PluginVersion)]
// ReSharper disable once ClassNeverInstantiated.Global
public class SaS2ModOptions : BasePlugin
{
    internal static SaS2ModOptions Instance;
    internal static readonly List<RegisteredConfig> RegisteredConfigs = [];

    private Harmony _harmony;
    private FileSystemWatcher _configWatcher;
    private Timer _debounceTimer;

    public override void Load()
    {
        Instance = this;

        var configDirectory = Path.GetDirectoryName(Config.ConfigFilePath);
        var configFileName  = Path.GetFileName(Config.ConfigFilePath);
        if (!string.IsNullOrEmpty(configDirectory))
        {
            _configWatcher = new FileSystemWatcher(configDirectory, configFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _debounceTimer = new Timer(1000) { AutoReset = false };
            _debounceTimer.Elapsed += (_, _) =>
            {
                Config.Reload();
                Log.LogInfo("Configuration reloaded.");
            };
            _configWatcher.Changed += (_, _) => { _debounceTimer.Stop(); _debounceTimer.Start(); };
        }

        _harmony = new Harmony(PluginInfo.PluginGuid);
        _harmony.PatchAll();
        Log.LogInfo($"{PluginInfo.PluginName} v{PluginInfo.PluginVersion} loaded.");
    }

    public override bool Unload()
    {
        _configWatcher?.Dispose();
        _debounceTimer?.Dispose();
        _harmony?.UnpatchSelf();
        return base.Unload();
    }

    // ReSharper disable once UnusedMember.Global
    public static void RegisterConfig(ConfigEntryBase entry, string modName, string displayName, int order = 0)
    {
        RegisteredConfigs.Add(new RegisteredConfig(entry, modName, displayName, order));
    }

    public class RegisteredConfig(ConfigEntryBase entry, string modName, string displayName, int order = 0)
    {
        public ConfigEntryBase Entry { get; } = entry;
        public string ModName { get; } = modName;
        public string DisplayName { get; } = displayName;
        public int Order { get; } = order;
    }
}