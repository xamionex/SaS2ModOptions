using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.NET.Common;
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
        var configFileName = Path.GetFileName(Config.ConfigFilePath);
        if (!string.IsNullOrEmpty(configDirectory))
        {
            _configWatcher = new FileSystemWatcher(configDirectory, configFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size, EnableRaisingEvents = true
            };
            _debounceTimer = new Timer(1000) { AutoReset = false };
            _debounceTimer.Elapsed += (_, _) =>
            {
                Config.Reload();
                Log.LogInfo("Configuration reloaded.");
            };
            _configWatcher.Changed += (_, _) =>
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            };
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

    /// For a global config entry that was registered as perPlayer=true, returns the player-specific entry (creates it on first access, falling back to the global value).
    // ReSharper disable once UnusedMember.Global
    public static ConfigEntryBase GetPlayerConfigEntry(ConfigEntryBase globalEntry, int playerId)
    {
        var registered = RegisteredConfigs.FirstOrDefault(rc => rc.GlobalEntry == globalEntry);
        if (registered == null)
        {
            Instance.Log.LogError($"Config entry {globalEntry.Definition.Key} is not registered with SaS2ModOptions.");
            return globalEntry;
        }

        if (!registered.IsPerPlayer)
        {
            Instance.Log.LogWarning(
                $"Config entry {globalEntry.Definition.Key} is not per-player. Returning global entry.");
            return globalEntry;
        }

        return registered.GetEntryForPlayer(playerId);
    }

    /// <summary>
    /// Register a config entry to appear in the Mod Options menu.
    /// </summary>
    /// <param name="entry">The BepInEx config entry to expose.</param>
    /// <param name="modName">Section header shown in the menu (typically your mod's name).</param>
    /// <param name="displayName">Label shown next to the value.</param>
    /// <param name="order">Ascending sort order within the section.</param>
    /// <param name="perPlayer">
    ///     When true, each local-coop player gets their own independent copy of the setting.
    /// </param>
    /// <param name="acceptableValues">
    ///     Optional list of valid string values for <see cref="ConfigEntry{T}"/> entries whose
    ///     type is <see cref="string"/>. When provided, Left/Right cycle through the list.
    ///     Color strings (four comma-separated numbers) are handled separately and ignore this.
    /// </param>
    // ReSharper disable once UnusedMember.Global
    public static void RegisterConfig(ConfigEntryBase entry, string modName, string displayName, int order = 0,
        bool perPlayer = false, string[] acceptableValues = null)
    {
        RegisteredConfigs.Add(new RegisteredConfig(entry, modName, displayName, order, perPlayer, acceptableValues));
    }
    
    // Backward-compatible overload (4 params), calls the 5-param version with perPlayer = false
    // ReSharper disable once UnusedMember.Global
    public static void RegisterConfig(ConfigEntryBase entry, string modName, string displayName, int order)
    {
        RegisterConfig(entry, modName, displayName, order, false);
    }

    public class RegisteredConfig(
        ConfigEntryBase entry,
        string modName,
        string displayName,
        int order,
        bool perPlayer,
        string[] acceptableValues = null)
    {
        public ConfigEntryBase GlobalEntry { get; } = entry;
        public string ModName { get; } = modName;
        public string DisplayName { get; } = displayName;
        public int Order { get; } = order;
        public bool IsPerPlayer { get; } = perPlayer;

        /// <summary>
        /// When non-null and the entry type is <see cref="string"/>, Left/Right cycle through
        /// these values in the Mod Options menu.
        /// </summary>
        public string[] AcceptableValues { get; } = acceptableValues;

        private readonly Dictionary<int, ConfigEntryBase> _playerEntries = new();

        public ConfigEntryBase GetEntryForPlayer(int playerId)
        {
            if (!IsPerPlayer) return GlobalEntry;
            if (_playerEntries.TryGetValue(playerId, out var existing)) return existing;

            // Create player-specific key: "SettingName_P1", "SettingName_P2"
            var playerKey = $"{GlobalEntry.Definition.Key}_P{playerId + 1}";
            var configFile = GlobalEntry.ConfigFile;

            // Bind a new entry of the same type
            ConfigEntryBase playerEntry = GlobalEntry.SettingType switch
            {
                var t when t == typeof(bool) => configFile.Bind(GlobalEntry.Definition.Section, playerKey,
                    ((ConfigEntry<bool>)GlobalEntry).Value, GlobalEntry.Description),
                var t when t == typeof(int) => configFile.Bind(GlobalEntry.Definition.Section, playerKey,
                    ((ConfigEntry<int>)GlobalEntry).Value, GlobalEntry.Description),
                var t when t == typeof(float) => configFile.Bind(GlobalEntry.Definition.Section, playerKey,
                    ((ConfigEntry<float>)GlobalEntry).Value, GlobalEntry.Description),
                var t when t.IsEnum => configFile.Bind(GlobalEntry.Definition.Section, playerKey,
                    GlobalEntry.BoxedValue, GlobalEntry.Description),
                var t when t == typeof(string) => configFile.Bind(GlobalEntry.Definition.Section, playerKey,
                    (string)GlobalEntry.BoxedValue, GlobalEntry.Description),
                _ => throw new NotSupportedException($"Unsupported config type: {GlobalEntry.SettingType}")
            };
            _playerEntries[playerId] = playerEntry;
            return playerEntry;
        }
    }
}