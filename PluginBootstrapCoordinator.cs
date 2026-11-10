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
        ExpandWorldDataCompatibility.DetectDependency();
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
        PluginBoundSettings.DungeonSpawnDiagnostics = _host.BindConfigEntry(
            "5 - Diagnostics",
            "Dungeon spawn diagnostics",
            DropNSpawnPlugin.Toggle.Off,
            "Log bounded dungeon spawn diagnostics every 30 seconds. Local only: enable separately on the server and the clients reproducing the issue. Does not change spawn rules. Includes limited position/collider samples; turn off after testing.",
            synchronizedSetting: false);

        BindDomainConfigurationEntries();
    }

    private void BindDomainConfigurationEntries()
    {
        PluginBoundSettings.EnableCharacterOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable Character Overrides",
            DropNSpawnPlugin.Toggle.On,
            "Apply DNS creature-drop YAML. Off removes DNS overrides and keeps your YAML files; DNS stacking and nearby OnePerPlayer adjustments also stop.\n" +
            "Use Off if Drop That! or MonsterDB manages creature drops. MonsterDB stat or appearance edits alone do not require Off.\n" +
            "Global loot scaling, instant loot and the player-aligned killer filter have their own settings under 2 - Character.",
            synchronizedSetting: true,
            configManagerOrder: 700);
        PluginBoundSettings.EnableObjectOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable Object Overrides",
            DropNSpawnPlugin.Toggle.On,
            "Apply DNS YAML to chests, pickables, trees, rocks and other objects. Off removes DNS overrides and keeps your YAML files.\n" +
            "Use Off if Drop That! manages object loot. You can keep Character Overrides on if DNS still manages creature drops.",
            synchronizedSetting: true,
            configManagerOrder: 600);
        PluginBoundSettings.EnableSpawnerOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable Spawner Overrides",
            DropNSpawnPlugin.Toggle.On,
            "Apply DNS SpawnArea and CreatureSpawner settings. Off removes DNS overrides and keeps your YAML files.\n" +
            "Use Off if Spawn That! or MonsterDB manages these local spawners. Normal world spawns have a separate switch: Enable SpawnSystem Overrides.",
            synchronizedSetting: true,
            configManagerOrder: 500);
        PluginBoundSettings.EnableSpawnSystemOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable SpawnSystem Overrides",
            DropNSpawnPlugin.Toggle.On,
            "Use DNS YAML for normal world spawns. Off restores the captured spawn lists and disables DNS biome interval multipliers, extended global key checks and natural spawn limit correction. Your YAML files stay on disk.\n" +
            "Use Off if Spawn That! or MonsterDB manages world spawns.\n" +
            "Keep On if DNS manages world spawns alongside Expand World Data. Off does not turn EWD's overlapping spawn feature back on.",
            synchronizedSetting: true,
            configManagerOrder: 300);
        PluginBoundSettings.EnableEventOverrides = _host.BindConfigEntry(
            "4 - Domains",
            "Enable Event Overrides",
            DropNSpawnPlugin.Toggle.On,
            "Apply DNS raid definitions from YAML. Off removes DNS YAML overrides and keeps your files. Use Off if Custom Raids manages raids.\n" +
            "Settings under 3 - Events still apply: with Custom Raids, use Vanilla scheduling, Default event player base = Off, Event duration multiplier = 1, and matching chance/interval settings.\n" +
            "Expand World Data users do not need to turn this off for DNS raids. Off does not re-enable EWD events.",
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

    internal static void DetachReloadAndManifestHandlers(DropNSpawnPlugin host)
    {
        if (host.ReloadCoordinator is PluginReloadCoordinator reloadCoordinator)
        {
            DropNSpawnPlugin.ConfigSync.SourceOfTruthChanged -= reloadCoordinator.HandleSourceOfTruthChanged;
            PluginBoundSettings.EnableObjectOverrides?.SettingChanged -= reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.EnableCharacterOverrides?.SettingChanged -= reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.EnableSpawnerOverrides?.SettingChanged -= reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.EnableSpawnSystemOverrides?.SettingChanged -= reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.EnableEventOverrides?.SettingChanged -= reloadCoordinator.HandleDomainToggleSettingChanged;
            PluginBoundSettings.DisableGlobalKeySpawnSystemEntriesInLowTierBiomes?.SettingChanged -= HandleSpawnSystemGlobalFilterSettingChanged;
        }

        PluginManifestCoordinator.DetachRuntimeDomainHandlers();
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
        DungeonSpawnManager.Initialize();
        DropNSpawnConsoleCommands.Register();
    }

    private void ApplyPatchesAndWatchers()
    {
        ExpandWorldDataCompatibility.Initialize(_host.HarmonyInstance);
        Assembly assembly = Assembly.GetExecutingAssembly();
        _host.HarmonyInstance.PatchAll(assembly);
        RagdollSetupMonsterInstantLootDropPatch.InitializeEpicLootCompatibility(_host.HarmonyInstance);
        EspSpawnSystemCompatibility.Initialize(_host.HarmonyInstance);
        VneiCompatibility.Initialize(_host.HarmonyInstance);
        _host.ReloadCoordinator!.InitializeWatchers();
    }
}
