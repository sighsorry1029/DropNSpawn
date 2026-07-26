using System;
using System.IO;
using System.Reflection;

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
        PluginBoundSettings.DisableGlobalKeySpawnSystemEntriesInLowTierBiomes = _host.BindConfigEntry(
            "1 - General",
            "Disable global key SpawnSystem entries in low tier biomes",
            DropNSpawnPlugin.Toggle.Off,
            "If on, SpawnSystem entries with requiredGlobalKey in Meadows, BlackForest, Swamp, Mountain, or Plains are disabled globally. This prevents global keys from making high tier monsters appear in low tier biomes.",
            synchronizedSetting: true,
            configManagerOrder: 700);
        SpawnerGlobalConfig.Bind(_host);
        CharacterDropGlobalConfig.Bind(_host);
        EventGlobalConfig.Bind(_host);

        BindDomainConfigurationEntries();
    }

    private void BindDomainConfigurationEntries()
    {
        PluginBoundSettings.EnableCharacterOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable Character Overrides",
            DropNSpawnPlugin.Toggle.On,
            "If off, DropNSpawn character YAML files stay on disk but CharacterDrop runtime overrides are not applied and existing character changes are restored to vanilla. Turn this off with Enable Object when using Drop That!. Turn this off when using Spawner Tweaks creature overrides.",
            synchronizedSetting: true,
            configManagerOrder: 700);
        PluginBoundSettings.EnableObjectOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable Object Overrides",
            DropNSpawnPlugin.Toggle.On,
            "If off, DropNSpawn object YAML files stay on disk but object runtime overrides are not applied and existing object changes are restored to vanilla. Turn this off with Enable Character when using Drop That!. Turn this off when using Spawner Tweaks features for Chests or Pickables.",
            synchronizedSetting: true,
            configManagerOrder: 600);
        PluginBoundSettings.EnableSpawnerOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable Spawner Overrides",
            DropNSpawnPlugin.Toggle.On,
            "If off, DropNSpawn SpawnArea and CreatureSpawner runtime overrides are not applied and existing spawner changes are restored to vanilla. Turn this off with Enable SpawnSystem when using Spawn That!. Turn this off when using Spawner Tweaks Spawn points or Spawners features.",
            synchronizedSetting: true,
            configManagerOrder: 500);
        PluginBoundSettings.EnableSpawnSystemOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable SpawnSystem Overrides",
            DropNSpawnPlugin.Toggle.On,
            "If off, DropNSpawn world SpawnSystem runtime overrides and extended global key handling are not applied and existing SpawnSystem changes are restored to vanilla. Turn this off for Expand World Spawns. Turn this off with Enable Spawner when using Spawn That! world spawning.",
            synchronizedSetting: true,
            configManagerOrder: 300);
        PluginBoundSettings.EnableEventOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable Event Overrides",
            DropNSpawnPlugin.Toggle.On,
            "If off, DropNSpawn event YAML files stay on disk but RandEventSystem event overrides are not applied and existing event changes are restored to vanilla.",
            synchronizedSetting: true,
            configManagerOrder: 200);
    }

    private void InitializeCoordinators()
    {
        _host.RuntimeWorkCoordinator = new PluginRuntimeWorkCoordinator(_host);
        _host.ReloadCoordinator = new PluginReloadCoordinator(
            _host,
            PluginBoundSettings.EnableObjectOverrides!,
            PluginBoundSettings.EnableCharacterOverrides!,
            PluginBoundSettings.EnableSpawnerOverrides!,
            PluginBoundSettings.EnableSpawnSystemOverrides!,
            PluginBoundSettings.EnableEventOverrides!);
    }

    private void AttachReloadAndManifestHandlers()
    {
        _ = DropNSpawnPlugin.ConfigSync.AddLockingConfigEntry(PluginBoundSettings.ServerConfigLocked!);
        DropNSpawnPlugin.ConfigSync.SourceOfTruthChanged += _host.ReloadCoordinator!.HandleSourceOfTruthChanged;
        PluginBoundSettings.EnableObjectOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.EnableCharacterOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.EnableSpawnerOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.EnableSpawnSystemOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.EnableEventOverrides!.SettingChanged += _host.ReloadCoordinator.HandleDomainToggleSettingChanged;
        PluginBoundSettings.DisableGlobalKeySpawnSystemEntriesInLowTierBiomes!.SettingChanged += HandleSpawnSystemGlobalFilterSettingChanged;
        PluginManifestCoordinator.AttachRuntimeDomainHandlers();
    }

    internal static void HandleSpawnSystemGlobalFilterSettingChanged(object? sender, EventArgs e)
    {
        SpawnSystemManager.ReloadConfiguration();
    }

    private void InitializeRuntimeSystems()
    {
        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        NetworkPayloadSyncSupport.Initialize(_host);
        ExampleContentWriter.EnsureDefaultExampleFiles();
        DomainRegistry.InitializeRuntimeDomains();
        DropNSpawnConsoleCommands.Register();
    }

    private void ApplyPatchesAndWatchers()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        _host.HarmonyInstance.PatchAll(assembly);
        EspSpawnSystemCompatibility.Initialize(_host.HarmonyInstance);
        VneiCompatibility.Initialize(_host.HarmonyInstance);
        _host.ReloadCoordinator!.InitializeWatchers();
    }
}
