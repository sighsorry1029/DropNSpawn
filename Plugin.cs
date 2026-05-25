using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace DropNSpawn;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency("expand_world_data")]
/// <summary>
/// Unity entrypoint and top-level wiring for the runtime platform.
/// Owns lifecycle delegation only; coordinators and domain runtimes own the actual mutable platform state.
/// </summary>
public class DropNSpawnPlugin : BaseUnityPlugin
{
    [Flags]
    internal enum ReloadDomain
    {
        None = 0,
        Object = 1 << 0,
        Character = 1 << 1,
        Spawner = 1 << 2,
        Location = 1 << 3,
        SpawnSystem = 1 << 4,
        All = Object | Character | Spawner | Location | SpawnSystem
    }

    internal readonly struct DomainToggleState
    {
        internal DomainToggleState(Toggle @object, Toggle character, Toggle spawner, Toggle location, Toggle spawnSystem)
        {
            Object = @object;
            Character = character;
            Spawner = spawner;
            Location = location;
            SpawnSystem = spawnSystem;
        }

        internal Toggle Object { get; }
        internal Toggle Character { get; }
        internal Toggle Spawner { get; }
        internal Toggle Location { get; }
        internal Toggle SpawnSystem { get; }
    }

    internal const string ModName = "DropNSpawn";
    internal const string YamlFilePrefix = "DNS";
    internal const string ModVersion = "1.2.2";
    internal const string Author = "sighsorry";
    private const string ModGUID = $"{Author}.{ModName}";
    internal static readonly string RuntimeBuildStamp = BuildRuntimeBuildStamp();
    private static string ConfigFileName = $"{ModGUID}.cfg";
    private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    internal static string YamlConfigDirectoryPath => Path.Combine(Paths.ConfigPath, ModName);
    internal static string YamlRulesWatcherPattern => $"{YamlFilePrefix}_*.*";
    internal static string CurrentConfigFileName => ConfigFileName;
    internal static string CurrentConfigFileFullPath => ConfigFileFullPath;
    internal static string ConnectionError = "";
    internal static DropNSpawnPlugin? Instance { get; private set; }
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource DropNSpawnLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static ConfigSync? _configSync;
    internal static ConfigSync ConfigSync => _configSync ?? throw new InvalidOperationException("ServerSync has not been initialized yet.");
    private PluginReloadCoordinator? _reloadCoordinator;
    private PluginRuntimeWorkCoordinator? _runtimeWorkCoordinator;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public enum ReferenceUpdateMode
    {
        AutoUpdate = 0,
        ManualUpdate = 1
    }

    public void Awake()
    {
        EnsureServerSyncInitialized();
        Instance = this;
        new PluginBootstrapCoordinator(this).Run();
    }

    private void Update()
    {
        _runtimeWorkCoordinator?.ProcessUpdateFrame();
    }

    private static string BuildRuntimeBuildStamp()
    {
        try
        {
            Assembly assembly = typeof(DropNSpawnPlugin).Assembly;
            string moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("N");
            return $"{ModVersion}+{moduleVersionId.Substring(0, 8)}";
        }
        catch
        {
            return ModVersion;
        }
    }


    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        SaveWithRespectToConfigSet();
        if (_configSync != null && _reloadCoordinator != null)
        {
            _configSync.SourceOfTruthChanged -= _reloadCoordinator.HandleSourceOfTruthChanged;
        }
        if (_reloadCoordinator != null)
        {
            PluginBoundSettings.EnableObjectOverrides?.SettingChanged -= _reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.EnableCharacterOverrides?.SettingChanged -= _reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.EnableSpawnerOverrides?.SettingChanged -= _reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.EnableLocationOverrides?.SettingChanged -= _reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.EnableSpawnSystemOverrides?.SettingChanged -= _reloadCoordinator.HandleDomainToggleSettingChanged;
        }
        PluginManifestCoordinator.DetachRuntimeDomainHandlers();
        _runtimeWorkCoordinator?.Dispose();
        _runtimeWorkCoordinator = null;
        _reloadCoordinator?.Dispose();
        _reloadCoordinator = null;
        PluginBoundSettings.Clear();

        NetworkPayloadSyncSupport.Shutdown();
        BossStonePerPlayerRuntime.Shutdown();
    }

    private static void EnsureServerSyncInitialized()
    {
        if (_configSync != null)
        {
            return;
        }

        ConfigSync configSync = new(ModGUID)
        {
            DisplayName = ModName,
            CurrentVersion = ModVersion,
            MinimumRequiredVersion = ModVersion
        };

        _configSync = configSync;
    }

    internal static string GetSyncedManifestValue(DomainDescriptor domain)
    {
        return PluginManifestCoordinator.GetSyncedManifestValue(domain);
    }

    internal void SaveWithRespectToConfigSet(bool reload = false, bool save = true)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            if (reload)
            {
                Config.Reload();
            }

            if (save)
            {
                Config.Save();
            }
        }
        finally
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
        
        // If you want to do something once localization completes, LocalizationManager has a hook for that.
        /*Localizer.OnLocalizationComplete += () =>
        {
            // Do something
            ItemManagerModTemplateLogger.LogDebug("OnLocalizationComplete called");
        };*/
    }

    internal static bool IsSourceOfTruth => ConfigSync.IsSourceOfTruth;

    internal static bool IsRuntimeServer()
    {
        return ZNet.instance != null && ZNet.instance.IsServer();
    }

    internal static void QueueGameDataRefresh(ReloadDomain domains, string source)
    {
        Instance?._runtimeWorkCoordinator?.QueueGameDataRefresh(domains, source);
    }

    internal static bool IsGameDataRefreshDeferred(ReloadDomain domain)
    {
        return Instance?._runtimeWorkCoordinator?.IsGameDataRefreshDeferred(domain) == true;
    }

    internal Harmony HarmonyInstance => _harmony;

    internal PluginReloadCoordinator? ReloadCoordinator
    {
        get => _reloadCoordinator;
        set => _reloadCoordinator = value;
    }

    internal PluginRuntimeWorkCoordinator? RuntimeWorkCoordinator
    {
        get => _runtimeWorkCoordinator;
        set => _runtimeWorkCoordinator = value;
    }

    internal static bool TryGetSyncedEntries<TEntry>(
        DomainDescriptor<TEntry> domain,
        out List<TEntry> entries,
        out string payloadToken)
    {
        return PluginManifestCoordinator.TryGetSyncedEntries(domain, out entries, out payloadToken);
    }

    internal static void PublishSyncedPayload<TEntry>(
        DomainDescriptor<TEntry> domain,
        List<TEntry> entries,
        string? knownSignature)
    {
        PluginManifestCoordinator.PublishSyncedPayload(domain, entries, knownSignature);
    }

    #region ConfigOptions

    internal ConfigEntry<T> BindConfigEntry<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true, int? configManagerOrder = null)
    {
        ConfigDescription extendedDescription = new(
            description.Description + (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
            description.AcceptableValues,
            BuildConfigDescriptionTags(description.Tags, configManagerOrder));
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        //var configEntry = Config.Bind(group, name, value, description);

        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
    {
        return BindConfigEntry(group, name, value, description, synchronizedSetting);
    }

    internal ConfigEntry<T> BindConfigEntry<T>(string group, string name, T value, string description, bool synchronizedSetting = true, int? configManagerOrder = null)
    {
        return BindConfigEntry(group, name, value, new ConfigDescription(description), synchronizedSetting, configManagerOrder);
    }

    private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
    {
        return BindConfigEntry(group, name, value, description, synchronizedSetting);
    }

    private class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order = null!;
        [UsedImplicitly] public bool? Browsable = null!;
        [UsedImplicitly] public string? Category = null!;
        [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
    }

    private static object[] BuildConfigDescriptionTags(object[]? existingTags, int? configManagerOrder)
    {
        if (!configManagerOrder.HasValue)
        {
            return existingTags ?? Array.Empty<object>();
        }

        return (existingTags ?? Array.Empty<object>())
            .Concat(new object[]
            {
                new ConfigurationManagerAttributes
                {
                    Order = configManagerOrder.Value
                }
            })
            .ToArray();
    }

    internal class AcceptableShortcuts() : AcceptableValueBase(typeof(KeyboardShortcut))
    {
        public override object Clamp(object value) => value;
        public override bool IsValid(object value) => true;

        public override string ToDescriptionString() => $"# Acceptable values: {string.Join(", ", UnityInput.Current.SupportedKeyCodes)}";
    }

    #endregion
}

public static class KeyboardExtensions
{
    extension(KeyboardShortcut shortcut)
    {
        public bool IsKeyDown()
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
        }

        public bool IsKeyHeld()
        {
            return shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
        }
    }
}

public static class ToggleExtentions
{
    extension(DropNSpawnPlugin.Toggle value)
    {
        public bool IsOn()
        {
            return value == DropNSpawnPlugin.Toggle.On;
        }

        public bool IsOff()
        {
            return value == DropNSpawnPlugin.Toggle.Off;
        }
    }
}
