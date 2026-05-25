using System;
using System.Collections;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace DropNSpawn;

/// <summary>
/// Owns watcher-driven reload state, debounce, and source-of-truth cutover behavior.
/// It delegates actual domain apply work to the registered domain runtimes.
/// </summary>
internal sealed class PluginReloadCoordinator
{
    private const float FileReloadDebounceSeconds = 0.5f;

    private readonly DropNSpawnPlugin _host;
    private readonly (ConfigEntry<DropNSpawnPlugin.Toggle> Entry, DropNSpawnPlugin.ReloadDomain Domain)[] _domainToggles;
    private readonly object _reloadLock = new();
    private readonly object _reloadDebounceLock = new();
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _rulesWatcher;
    private Coroutine? _configReloadCoroutine;
    private Coroutine? _rulesReloadCoroutine;
    private DropNSpawnPlugin.ReloadDomain _pendingRuleReloadDomains;
    private bool _configReloadPending;
    private float _lastQueuedConfigReloadTime;
    private float _lastQueuedRuleReloadTime;

    internal PluginReloadCoordinator(
        DropNSpawnPlugin host,
        ConfigEntry<DropNSpawnPlugin.Toggle> enableObjectOverrides,
        ConfigEntry<DropNSpawnPlugin.Toggle> enableCharacterOverrides,
        ConfigEntry<DropNSpawnPlugin.Toggle> enableSpawnerOverrides,
        ConfigEntry<DropNSpawnPlugin.Toggle> enableLocationOverrides,
        ConfigEntry<DropNSpawnPlugin.Toggle> enableSpawnSystemOverrides)
    {
        _host = host;
        _domainToggles = new[]
        {
            (enableObjectOverrides, DropNSpawnPlugin.ReloadDomain.Object),
            (enableCharacterOverrides, DropNSpawnPlugin.ReloadDomain.Character),
            (enableSpawnerOverrides, DropNSpawnPlugin.ReloadDomain.Spawner),
            (enableLocationOverrides, DropNSpawnPlugin.ReloadDomain.Location),
            (enableSpawnSystemOverrides, DropNSpawnPlugin.ReloadDomain.SpawnSystem)
        };
    }

    internal bool IsConfigEntryReloadSuppressed { get; private set; }

    internal void HandleSourceOfTruthChanged(bool isSourceOfTruth)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogDebug($"Config source of truth changed. Local is owner: {isSourceOfTruth}.");
        NetworkPayloadSyncSupport.HandleSourceOfTruthChanged(isSourceOfTruth);
        if (!isSourceOfTruth)
        {
            PluginManifestCoordinator.EnterClientAuthorityCutover();
            PluginManifestCoordinator.ReplayCurrentSyncedManifestStates();
        }

        UpdateRulesWatcherState(DropNSpawnPlugin.IsSourceOfTruth);
        ReloadDomains(DropNSpawnPlugin.ReloadDomain.All);
    }

    internal void HandleDomainToggleSettingChanged(object? sender, EventArgs e)
    {
        if (IsConfigEntryReloadSuppressed)
        {
            return;
        }

        ReloadDomains(GetReloadDomainForToggleSetting(sender));
    }

    internal void InitializeWatchers()
    {
        SetupConfigWatcher();
        SetupRulesWatcher();
    }

    internal void Dispose()
    {
        _configWatcher?.Dispose();
        _rulesWatcher?.Dispose();
        if (_configReloadCoroutine != null)
        {
            _host.StopCoroutine(_configReloadCoroutine);
            _configReloadCoroutine = null;
        }

        if (_rulesReloadCoroutine != null)
        {
            _host.StopCoroutine(_rulesReloadCoroutine);
            _rulesReloadCoroutine = null;
        }
    }

    internal void UpdateRulesWatcherState(bool isSourceOfTruth)
    {
        if (_rulesWatcher == null)
        {
            return;
        }

        _rulesWatcher.EnableRaisingEvents = isSourceOfTruth;
    }

    private void SetupConfigWatcher()
    {
        _configWatcher = new FileSystemWatcher(Paths.ConfigPath, DropNSpawnPlugin.CurrentConfigFileName);
        _configWatcher.Changed += OnConfigFileChanged;
        _configWatcher.Created += OnConfigFileChanged;
        _configWatcher.Renamed += OnConfigFileChanged;
        _configWatcher.IncludeSubdirectories = true;
        _configWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _configWatcher.EnableRaisingEvents = true;
    }

    private void SetupRulesWatcher()
    {
        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        _rulesWatcher = new FileSystemWatcher(DropNSpawnPlugin.YamlConfigDirectoryPath, ObjectDropManager.RulesWatcherPattern);
        _rulesWatcher.Changed += OnRuleFileChanged;
        _rulesWatcher.Created += OnRuleFileChanged;
        _rulesWatcher.Deleted += OnRuleFileChanged;
        _rulesWatcher.Renamed += OnRuleFileChanged;
        _rulesWatcher.IncludeSubdirectories = true;
        _rulesWatcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        UpdateRulesWatcherState(DropNSpawnPlugin.IsSourceOfTruth);
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        QueueConfigReload();
    }

    private void OnRuleFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        DropNSpawnPlugin.ReloadDomain domains = DropNSpawnPlugin.ReloadDomain.None;
        foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
        {
            bool shouldReload = domain.ShouldReloadForPath(e.FullPath);
            if (!shouldReload && e is RenamedEventArgs renamedEvent)
            {
                shouldReload = domain.ShouldReloadForPath(renamedEvent.OldFullPath);
            }

            if (shouldReload)
            {
                domains |= domain.ReloadDomain;
            }
        }

        if (domains != DropNSpawnPlugin.ReloadDomain.None)
        {
            QueueRuleReload(domains);
        }
    }

    private void QueueConfigReload()
    {
        lock (_reloadDebounceLock)
        {
            _configReloadPending = true;
            _lastQueuedConfigReloadTime = Time.realtimeSinceStartup;
            if (_configReloadCoroutine != null)
            {
                return;
            }

            _configReloadCoroutine = _host.StartCoroutine(ProcessQueuedConfigReload());
        }
    }

    private void QueueRuleReload(DropNSpawnPlugin.ReloadDomain domains)
    {
        if (domains == DropNSpawnPlugin.ReloadDomain.None)
        {
            return;
        }

        lock (_reloadDebounceLock)
        {
            _pendingRuleReloadDomains |= domains;
            _lastQueuedRuleReloadTime = Time.realtimeSinceStartup;
            if (_rulesReloadCoroutine != null)
            {
                return;
            }

            _rulesReloadCoroutine = _host.StartCoroutine(ProcessQueuedRuleReload());
        }
    }

    private IEnumerator ProcessQueuedConfigReload()
    {
        while (true)
        {
            float queuedAt;
            lock (_reloadDebounceLock)
            {
                queuedAt = _lastQueuedConfigReloadTime;
            }

            while (Time.realtimeSinceStartup - queuedAt < FileReloadDebounceSeconds)
            {
                yield return null;
                lock (_reloadDebounceLock)
                {
                    queuedAt = _lastQueuedConfigReloadTime;
                }
            }

            bool shouldReload;
            lock (_reloadDebounceLock)
            {
                shouldReload = _configReloadPending;
                _configReloadPending = false;
            }

            if (shouldReload)
            {
                ExecuteQueuedConfigReload();
            }

            lock (_reloadDebounceLock)
            {
                if (_configReloadPending)
                {
                    continue;
                }

                _configReloadCoroutine = null;
            }

            yield break;
        }
    }

    private IEnumerator ProcessQueuedRuleReload()
    {
        while (true)
        {
            float queuedAt;
            lock (_reloadDebounceLock)
            {
                queuedAt = _lastQueuedRuleReloadTime;
            }

            while (Time.realtimeSinceStartup - queuedAt < FileReloadDebounceSeconds)
            {
                yield return null;
                lock (_reloadDebounceLock)
                {
                    queuedAt = _lastQueuedRuleReloadTime;
                }
            }

            DropNSpawnPlugin.ReloadDomain domains;
            lock (_reloadDebounceLock)
            {
                domains = _pendingRuleReloadDomains;
                _pendingRuleReloadDomains = DropNSpawnPlugin.ReloadDomain.None;
            }

            if (domains != DropNSpawnPlugin.ReloadDomain.None)
            {
                ExecuteQueuedRuleReload(domains);
            }

            lock (_reloadDebounceLock)
            {
                if (_pendingRuleReloadDomains != DropNSpawnPlugin.ReloadDomain.None)
                {
                    continue;
                }

                _rulesReloadCoroutine = null;
            }

            yield break;
        }
    }

    private void ExecuteQueuedConfigReload()
    {
        lock (_reloadLock)
        {
            if (!File.Exists(DropNSpawnPlugin.CurrentConfigFileFullPath))
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogDebug("Reloading configuration...");
                DropNSpawnPlugin.DomainToggleState previousState = CaptureDomainToggleState();
                IsConfigEntryReloadSuppressed = true;
                _host.SaveWithRespectToConfigSet(reload: true, save: false);
                ReloadDomains(
                    GetChangedDomainToggles(
                        previousState,
                        CaptureDomainToggleState()));
                DropNSpawnPlugin.DropNSpawnLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
            finally
            {
                IsConfigEntryReloadSuppressed = false;
            }
        }
    }

    private void ExecuteQueuedRuleReload(DropNSpawnPlugin.ReloadDomain domains)
    {
        if (domains == DropNSpawnPlugin.ReloadDomain.None || !DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        lock (_reloadLock)
        {
            try
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogDebug("Reloading override YAML configuration...");
                ReloadDomains(domains);
                DropNSpawnPlugin.DropNSpawnLogger.LogInfo("Override YAML configuration reload complete.");
            }
            catch (Exception ex)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError($"Error reloading override YAML configuration: {ex.Message}");
            }
        }
    }

    private DropNSpawnPlugin.DomainToggleState CaptureDomainToggleState()
    {
        return new DropNSpawnPlugin.DomainToggleState(
            GetDomainToggleValue(DropNSpawnPlugin.ReloadDomain.Object),
            GetDomainToggleValue(DropNSpawnPlugin.ReloadDomain.Character),
            GetDomainToggleValue(DropNSpawnPlugin.ReloadDomain.Spawner),
            GetDomainToggleValue(DropNSpawnPlugin.ReloadDomain.Location),
            GetDomainToggleValue(DropNSpawnPlugin.ReloadDomain.SpawnSystem));
    }

    private DropNSpawnPlugin.ReloadDomain GetReloadDomainForToggleSetting(object? sender)
    {
        DropNSpawnPlugin.ReloadDomain domains = DropNSpawnPlugin.ReloadDomain.None;
        foreach ((ConfigEntry<DropNSpawnPlugin.Toggle> entry, DropNSpawnPlugin.ReloadDomain domain) in _domainToggles)
        {
            if (ReferenceEquals(sender, entry))
            {
                domains |= domain;
            }
        }

        return domains;
    }

    private DropNSpawnPlugin.Toggle GetDomainToggleValue(DropNSpawnPlugin.ReloadDomain domain)
    {
        foreach ((ConfigEntry<DropNSpawnPlugin.Toggle> entry, DropNSpawnPlugin.ReloadDomain toggleDomain) in _domainToggles)
        {
            if (toggleDomain == domain)
            {
                return entry.Value;
            }
        }

        return DropNSpawnPlugin.Toggle.Off;
    }

    private static DropNSpawnPlugin.ReloadDomain GetChangedDomainToggles(
        DropNSpawnPlugin.DomainToggleState previous,
        DropNSpawnPlugin.DomainToggleState current)
    {
        DropNSpawnPlugin.ReloadDomain domains = DropNSpawnPlugin.ReloadDomain.None;
        if (previous.Object != current.Object)
        {
            domains |= DropNSpawnPlugin.ReloadDomain.Object;
        }

        if (previous.Character != current.Character)
        {
            domains |= DropNSpawnPlugin.ReloadDomain.Character;
        }

        if (previous.Spawner != current.Spawner)
        {
            domains |= DropNSpawnPlugin.ReloadDomain.Spawner;
        }

        if (previous.Location != current.Location)
        {
            domains |= DropNSpawnPlugin.ReloadDomain.Location;
        }

        if (previous.SpawnSystem != current.SpawnSystem)
        {
            domains |= DropNSpawnPlugin.ReloadDomain.SpawnSystem;
        }

        return domains;
    }

    private static void ReloadDomains(DropNSpawnPlugin.ReloadDomain domains)
    {
        foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
        {
            if ((domains & domain.ReloadDomain) != 0)
            {
                domain.Reload();
            }
        }
    }
}
