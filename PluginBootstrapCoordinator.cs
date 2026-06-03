using System;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using ServerSync;
using UnityEngine;

namespace DropNSpawn;

/// <summary>
/// Startup coordinator that binds configuration, creates platform coordinators, and brings runtime systems online.
/// It does not own reload or per-domain runtime state after bootstrap completes.
/// </summary>
internal sealed class PluginBootstrapCoordinator
{
    private readonly DropNSpawnPlugin _host;

    internal PluginBootstrapCoordinator(DropNSpawnPlugin host)
    {
        _host = host;
    }

    internal void Run()
    {
        PluginManifestCoordinator.Initialize(DropNSpawnPlugin.ConfigSync);

        bool saveOnSet = _host.Config.SaveOnConfigSet;
        _host.Config.SaveOnConfigSet = false;
        try
        {
            BindConfigurationEntries();
            InitializeCoordinators();
            AttachReloadAndManifestHandlers();
            InitializeRuntimeSystems();
            ApplyPatchesAndWatchers();
            _host.Config.Save();
        }
        finally
        {
            if (saveOnSet)
            {
                _host.Config.SaveOnConfigSet = saveOnSet;
            }
        }
    }

    private void BindConfigurationEntries()
    {
        PluginBoundSettings.ServerConfigLocked = _host.BindConfigEntry(
            "1 - General",
            "Lock Configuration",
            DropNSpawnPlugin.Toggle.On,
            "If on, the configuration is locked and can be changed by server admins only.",
            configManagerOrder: 800);
        PluginBoundSettings.EnableCharacterOverrides = _host.BindConfigEntry(
            "5 - Domains",
            "Enable Character Overrides",
            GetLegacyDomainToggleDefault("Enable Character Overrides", DropNSpawnPlugin.Toggle.On),
            "If off, DropNSpawn character YAML files stay on disk but CharacterDrop runtime overrides are not applied and existing character changes are restored to vanilla. Turn this off with Enable Object when using Drop That!. Turn this off when using Spawner Tweaks creature overrides.",
            synchronizedSetting: true,
            configManagerOrder: 700);
        PluginBoundSettings.EnableObjectOverrides = _host.BindConfigEntry(
            "5 - Domains",
            "Enable Object Overrides",
            GetLegacyDomainToggleDefault("Enable Object Overrides", DropNSpawnPlugin.Toggle.On),
            "If off, DropNSpawn object YAML files stay on disk but object runtime overrides are not applied and existing object changes are restored to vanilla. Turn this off with Enable Character when using Drop That!. Turn this off when using Spawner Tweaks features for Chests or Pickables.",
            synchronizedSetting: true,
            configManagerOrder: 600);
        PluginBoundSettings.EnableSpawnerOverrides = _host.BindConfigEntry(
            "5 - Domains",
            "Enable Spawner Overrides",
            GetLegacyDomainToggleDefault("Enable Spawner Overrides", DropNSpawnPlugin.Toggle.On),
            "If off, DropNSpawn SpawnArea and CreatureSpawner runtime overrides are not applied and existing spawner changes are restored to vanilla. Turn this off with Enable SpawnSystem when using Spawn That!. Turn this off when using Spawner Tweaks Spawn points or Spawners features.",
            synchronizedSetting: true,
            configManagerOrder: 500);
        SpawnerGlobalConfig.Bind(_host);
        PluginBoundSettings.EnableLocationOverrides = _host.BindConfigEntry(
            "5 - Domains",
            "Enable Location Overrides",
            GetLegacyDomainToggleDefault("Enable Location Overrides", DropNSpawnPlugin.Toggle.On),
            "If off, DropNSpawn location runtime overrides for OfferingBowl, ItemStand, and Vegvisir are not applied and existing location changes are restored to vanilla. Turn this off when using Spawner Tweaks Boss altars or Item stands features.",
            synchronizedSetting: true,
            configManagerOrder: 400);
        PluginBoundSettings.EnableSpawnSystemOverrides = _host.BindConfigEntry(
            "5 - Domains",
            "Enable SpawnSystem Overrides",
            GetLegacyDomainToggleDefault("Enable SpawnSystem Overrides", DropNSpawnPlugin.Toggle.On),
            "If off, DropNSpawn world SpawnSystem runtime overrides and extended global key handling are not applied and existing SpawnSystem changes are restored to vanilla. Turn this off for Expand World Spawns. Turn this off with Enable Spawner when using Spawn That! world spawning.",
            synchronizedSetting: true,
            configManagerOrder: 300);
        RemoveOrphanedConfigEntry("1 - General", "Afternoon Start Fraction");
        PluginBoundSettings.ShowLocationProxyOfferingBowlHoverInfo = _host.BindConfigEntry(
            "2 - Boss",
            "Show LocationProxy Offering Bowl Hover Info",
            DropNSpawnPlugin.Toggle.On,
            "If on, looking at an OfferingBowl shows simplified offering info with the spawned boss/item and required offering item. Matching altar ItemStands also show their required supported item names.",
            synchronizedSetting: true);
        PluginBoundSettings.PerPlayerBossStones = _host.BindConfigEntry(
            "2 - Boss",
            "Per Player Boss Stones",
            DropNSpawnPlugin.Toggle.On,
            "Each player sees their own version of reality. Any player standing in the Start Temple when a trophy is sacrificed will have the trophy hung in their reality as well. Any player not standing in the Start Temple when a trophy is sacrificed will not see the trophy in their reality.",
            synchronizedSetting: true);
        PluginBoundSettings.RemoteForsakenPowerSelection = _host.BindConfigEntry(
            "2 - Boss",
            "Remote Forsaken Power Selection",
            DropNSpawnPlugin.Toggle.On,
            "If on, players can rotate through Forsaken Powers they have unlocked through per-player boss stones without returning to the Start Temple.",
            synchronizedSetting: true);

        BossRulesConfig.Bind(_host);
        DespawnRulesConfig.Bind(_host);
        CharacterDropGlobalConfig.Bind(_host);
        LocationRunestoneGlobalPinsConfig.Bind(_host);

        PluginBoundSettings.ReferenceUpdateMode = _host.BindConfigEntry(
            "4 - Client",
            "Reference Update Mode",
            DropNSpawnPlugin.ReferenceUpdateMode.AutoUpdate,
            $"AutoUpdate automatically creates missing reference YAML files and updates existing ones, except {DropNSpawnPlugin.YamlFilePrefix}_spawner.locations.reference.yml, which is only auto-created when missing and never auto-updated afterwards. {DropNSpawnPlugin.YamlFilePrefix}_spawnsystem.reference.yml is always manual-export-only. ManualUpdate automatically creates missing reference YAML files but updates existing ones only when you run dns:reference, while {DropNSpawnPlugin.YamlFilePrefix}_spawnsystem.reference.yml still remains manual-export-only.",
            synchronizedSetting: false);
        PluginBoundSettings.RotateForsakenPowerShortcut = _host.BindConfigEntry(
            "4 - Client",
            "Rotate Forsaken Power Shortcut",
            new KeyboardShortcut(KeyCode.G),
            new ConfigDescription(
                "Shortcut used to rotate through unlocked Forsaken Powers when Remote Forsaken Power Selection is enabled. This setting is client-side only.",
                new DropNSpawnPlugin.AcceptableShortcuts()),
            synchronizedSetting: false);
    }

    private DropNSpawnPlugin.Toggle GetLegacyDomainToggleDefault(string name, DropNSpawnPlugin.Toggle fallback)
    {
        ConfigDefinition legacyDefinition = new("1 - General", name);
        ConfigEntry<DropNSpawnPlugin.Toggle> legacyEntry = _host.Config.Bind(
            legacyDefinition,
            fallback,
            new ConfigDescription("Legacy domain toggle migrated to 5 - Domains."));
        DropNSpawnPlugin.Toggle value = legacyEntry.Value;
        _host.Config.Remove(legacyDefinition);
        return value;
    }

    private void RemoveOrphanedConfigEntry(string section, string key)
    {
        PropertyInfo? orphanedEntriesProperty = typeof(ConfigFile).GetProperty(
            "OrphanedEntries",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object? orphanedEntries = orphanedEntriesProperty?.GetValue(_host.Config);
        MethodInfo? removeMethod = orphanedEntries?.GetType().GetMethod("Remove", new[] { typeof(ConfigDefinition) });
        removeMethod?.Invoke(orphanedEntries, new object[] { new ConfigDefinition(section, key) });
    }

    private void InitializeCoordinators()
    {
        _host.RuntimeWorkCoordinator = new PluginRuntimeWorkCoordinator(_host);
        _host.ReloadCoordinator = new PluginReloadCoordinator(
            _host,
            PluginBoundSettings.EnableObjectOverrides!,
            PluginBoundSettings.EnableCharacterOverrides!,
            PluginBoundSettings.EnableSpawnerOverrides!,
            PluginBoundSettings.EnableLocationOverrides!,
            PluginBoundSettings.EnableSpawnSystemOverrides!);
    }

    private void AttachReloadAndManifestHandlers()
    {
        _ = DropNSpawnPlugin.ConfigSync.AddLockingConfigEntry(PluginBoundSettings.ServerConfigLocked!);
        DropNSpawnPlugin.ConfigSync.SourceOfTruthChanged += _host.ReloadCoordinator!.HandleSourceOfTruthChanged;
        PluginBoundSettings.EnableObjectOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.EnableCharacterOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.EnableSpawnerOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.EnableLocationOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.EnableSpawnSystemOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginManifestCoordinator.AttachRuntimeDomainHandlers();
    }

    private void InitializeRuntimeSystems()
    {
        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        NetworkPayloadSyncSupport.Initialize(_host);
        ExampleContentWriter.EnsureDefaultExampleFiles();
        DomainRegistry.InitializeRuntimeDomains();
        BossStonePerPlayerRuntime.Initialize();
        DropNSpawnConsoleCommands.Register();

    }

    private void ApplyPatchesAndWatchers()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        _host.HarmonyInstance.PatchAll(assembly);
        VneiCompatibility.Initialize(_host.HarmonyInstance);
        _host.ReloadCoordinator!.InitializeWatchers();
    }
}
