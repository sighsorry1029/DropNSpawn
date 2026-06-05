using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private const int RunestoneGlobalPinRpcVersion = 1;
    private const string RunestoneGlobalPinRequestRpc = "DropNSpawn Runestone GlobalPin Request";
    private const string RunestoneGlobalPinResponseRpc = "DropNSpawn Runestone GlobalPin Response";
    private static readonly ConditionalWeakTable<RuneStone, RunestoneGlobalPinsRollState> RunestoneGlobalPinsRolls = new();
    private static readonly object RunestoneGlobalPinsLock = new();
    private static readonly System.Random RunestoneGlobalPinsRandom = new();
    private static readonly Dictionary<string, ResolvedRunestoneGlobalPin?> RunestoneGlobalPinServerRolls =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> RunestoneGlobalPinDefaultPinNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RunestoneGlobalPinWarningLogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AccessTools.FieldRef<ZRoutedRpc, long> RunestoneGlobalPinRoutedRpcIdRef =
        AccessTools.FieldRefAccess<ZRoutedRpc, long>("m_id");
    private static RunestoneGlobalPinLocationIndex? RunestoneGlobalPinLocationIndexCache;
    private static ZRoutedRpc? RunestoneGlobalPinRegisteredRpcInstance;
    private static readonly FieldInfo? MinimapPinsField = AccessTools.Field(typeof(Minimap), "m_pins");

    private sealed class RunestoneGlobalPinsRollState
    {
        public string RollKey { get; set; } = "";
        public ResolvedRunestoneGlobalPin? Pin { get; set; }
    }

    private sealed class RunestoneGlobalPinCandidate
    {
        public float Chance { get; set; }
        public ResolvedRunestoneGlobalPin Pin { get; set; } = new();
    }

    private sealed class ResolvedRunestoneGlobalPin
    {
        public string LocationName { get; set; } = "";
        public string PinName { get; set; } = "";
        public Minimap.PinType PinType { get; set; } = Minimap.PinType.Icon3;
        public Vector3 Position { get; set; }
    }

    private sealed class RunestoneGlobalPinLocationIndex
    {
        public int ZoneSystemId { get; set; }
        public int LocationInstanceCount { get; set; }
        public Dictionary<string, List<ZoneSystem.LocationInstance>> InstancesByName { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal static void TryApplyRunestoneGlobalPins(RuneStone? runestone, bool hold, string? originalLocationName)
    {
        if (hold ||
            runestone == null ||
            runestone.gameObject == null ||
            !PluginSettingsFacade.IsLocationDomainEnabled() ||
            !PluginSettingsFacade.IsRunestoneGlobalPinsEnabled())
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(originalLocationName ?? runestone.m_locationName))
        {
            return;
        }

        if (TryRequestServerResolvedRunestoneGlobalPin(runestone, originalLocationName))
        {
            return;
        }

        LocationRunestoneGlobalPinsDefinition? definition = GetEffectiveRunestoneGlobalPinsDefinition();
        if (definition?.TargetLocations == null || definition.TargetLocations.Count == 0)
        {
            return;
        }

        ResolvedRunestoneGlobalPin? pin = GetOrRollRunestoneGlobalPin(runestone, definition, originalLocationName);
        if (pin != null)
        {
            TryAddRunestoneGlobalPin(pin);
        }
    }

    internal static void EnsureRunestoneGlobalPinRpcRegistered()
    {
        ZRoutedRpc? rpc = ZRoutedRpc.instance;
        if (rpc == null || ReferenceEquals(rpc, RunestoneGlobalPinRegisteredRpcInstance))
        {
            return;
        }

        if (RunestoneGlobalPinRegisteredRpcInstance != null)
        {
            ResetRunestoneGlobalPinsRuntimeState();
        }

        rpc.Register<ZPackage>(RunestoneGlobalPinRequestRpc, OnRunestoneGlobalPinRequestRpc);
        rpc.Register<ZPackage>(RunestoneGlobalPinResponseRpc, OnRunestoneGlobalPinResponseRpc);
        RunestoneGlobalPinRegisteredRpcInstance = rpc;
    }

    private static void ResetRunestoneGlobalPinsRuntimeState()
    {
        lock (RunestoneGlobalPinsLock)
        {
            RunestoneGlobalPinServerRolls.Clear();
        }

        RunestoneGlobalPinLocationIndexCache = null;
    }

    private static LocationRunestoneGlobalPinsDefinition? GetEffectiveRunestoneGlobalPinsDefinition()
    {
        for (int index = _configuration.Count - 1; index >= 0; index--)
        {
            LocationRunestoneGlobalPinsDefinition? definition = _configuration[index].RunestoneGlobalPins;
            if (HasRunestoneGlobalPinsOverride(definition))
            {
                return definition;
            }
        }

        return null;
    }

    private static ResolvedRunestoneGlobalPin? GetOrRollRunestoneGlobalPin(
        RuneStone runestone,
        LocationRunestoneGlobalPinsDefinition definition,
        string? originalLocationName)
    {
        string rollKey = CreateRunestoneGlobalPinsRollKey(runestone, definition, originalLocationName);
        lock (RunestoneGlobalPinsLock)
        {
            if (!RunestoneGlobalPinsRolls.TryGetValue(runestone, out RunestoneGlobalPinsRollState state))
            {
                state = new RunestoneGlobalPinsRollState();
                RunestoneGlobalPinsRolls.Add(runestone, state);
            }

            if (state.RollKey == rollKey)
            {
                return state.Pin;
            }

            state.RollKey = rollKey;
            state.Pin = RollRunestoneGlobalPin(runestone, definition);
            return state.Pin;
        }
    }

    private static ResolvedRunestoneGlobalPin? GetOrRollServerRunestoneGlobalPin(
        Vector3 runestonePosition,
        LocationRunestoneGlobalPinsDefinition definition,
        string? runestoneLocationName)
    {
        string rollKey = CreateRunestoneGlobalPinsServerRollKey(runestonePosition, definition, runestoneLocationName);
        lock (RunestoneGlobalPinsLock)
        {
            if (RunestoneGlobalPinServerRolls.TryGetValue(rollKey, out ResolvedRunestoneGlobalPin? cachedPin))
            {
                return cachedPin;
            }

            ResolvedRunestoneGlobalPin? pin = RollRunestoneGlobalPin(runestonePosition, definition);
            RunestoneGlobalPinServerRolls[rollKey] = pin;
            return pin;
        }
    }

    private static ResolvedRunestoneGlobalPin? RollRunestoneGlobalPin(
        RuneStone runestone,
        LocationRunestoneGlobalPinsDefinition definition)
    {
        return RollRunestoneGlobalPin(runestone.transform.position, definition);
    }

    private static ResolvedRunestoneGlobalPin? RollRunestoneGlobalPin(
        Vector3 runestonePosition,
        LocationRunestoneGlobalPinsDefinition definition)
    {
        if (definition.TargetLocations == null || ZoneSystem.instance == null)
        {
            return null;
        }

        List<RunestoneGlobalPinCandidate> candidates = new();
        Heightmap.Biome runestoneBiome = GetRunestoneGlobalPinBiome(runestonePosition);
        foreach (LocationRunestoneGlobalPinTargetDefinition target in definition.TargetLocations)
        {
            float chance = Mathf.Clamp01(target.Chance ?? 0f);
            if (target.LocationName.Length == 0 || chance <= 0f)
            {
                continue;
            }

            Heightmap.Biome allowedSourceBiomeMask = ResolveRunestoneGlobalPinSourceBiomeMask(target);
            if (!TryFindClosestRunestoneGlobalPinLocation(
                    target.LocationName,
                    runestonePosition,
                    runestoneBiome,
                    allowedSourceBiomeMask,
                    out ZoneSystem.LocationInstance locationInstance))
            {
                continue;
            }

            ResolvedRunestoneGlobalPin? pin = CreateResolvedRunestoneGlobalPin(target, locationInstance);
            if (pin != null)
            {
                candidates.Add(new RunestoneGlobalPinCandidate
                {
                    Chance = chance,
                    Pin = pin
                });
            }
        }

        return SelectRunestoneGlobalPinCandidate(candidates);
    }

    private static ResolvedRunestoneGlobalPin? SelectRunestoneGlobalPinCandidate(List<RunestoneGlobalPinCandidate> candidates)
    {
        float totalChance = 0f;
        foreach (RunestoneGlobalPinCandidate candidate in candidates)
        {
            totalChance += candidate.Chance;
        }

        if (totalChance <= 0f)
        {
            return null;
        }

        if (totalChance > 1f)
        {
            WarnRunestoneGlobalPin(
                $"globalpin|chance-total-over-1|{totalChance.ToString("R", CultureInfo.InvariantCulture)}",
                $"Runestone global pin chance values add up to {totalChance.ToString("0.###", CultureInfo.InvariantCulture)} after filtering. Normalizing them, so exactly one candidate can be selected.");
        }

        float rollRange = Math.Max(1f, totalChance);
        double roll = RunestoneGlobalPinsRandom.NextDouble() * rollRange;
        float cursor = 0f;
        foreach (RunestoneGlobalPinCandidate candidate in candidates)
        {
            cursor += candidate.Chance;
            if (roll < cursor)
            {
                return candidate.Pin;
            }
        }

        return null;
    }

    private static ResolvedRunestoneGlobalPin? CreateResolvedRunestoneGlobalPin(
        LocationRunestoneGlobalPinTargetDefinition target,
        ZoneSystem.LocationInstance locationInstance)
    {
        string pinName = !string.IsNullOrWhiteSpace(target.PinName)
            ? target.PinName!.Trim()
            : GetRunestoneGlobalPinDefaultPinName(target.LocationName);
        Minimap.PinType pinType = ParseRunestoneGlobalPinType(target.PinType, target.LocationName);

        return new ResolvedRunestoneGlobalPin
        {
            LocationName = target.LocationName,
            PinName = pinName,
            PinType = pinType,
            Position = locationInstance.m_position
        };
    }

    private static bool TryAddRunestoneGlobalPin(ResolvedRunestoneGlobalPin pin)
    {
        if (Minimap.instance == null || Player.m_localPlayer == null)
        {
            return false;
        }

        Minimap.PinType pinType = pin.PinType;

        if (HasSimilarRunestoneGlobalPin(pin.Position, pinType, pin.PinName, save: true))
        {
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_pin_exist");
            Minimap.instance.ShowPointOnMap(pin.Position);
            return false;
        }

        Minimap.PinData pinData = Minimap.instance.AddPin(pin.Position, pinType, pin.PinName, save: true, isChecked: false, 0L);
        Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "$msg_pin_added: " + pin.PinName, 0, pinData.m_icon);
        Minimap.instance.ShowPointOnMap(pin.Position);
        return true;
    }

    private static bool TryFindClosestRunestoneGlobalPinLocation(
        string locationName,
        Vector3 runestonePosition,
        Heightmap.Biome runestoneBiome,
        Heightmap.Biome allowedSourceBiomeMask,
        out ZoneSystem.LocationInstance closest)
    {
        closest = default;
        if (!TryGetRunestoneGlobalPinLocationInstances(locationName, out List<ZoneSystem.LocationInstance> candidates))
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        bool found = false;
        foreach (ZoneSystem.LocationInstance candidate in candidates)
        {
            Heightmap.Biome targetBiome = GetRunestoneGlobalPinBiome(candidate.m_position);
            if (!RunestoneGlobalPinBiomesMatch(targetBiome, runestoneBiome) &&
                !RunestoneGlobalPinAllowsSourceBiome(runestoneBiome, allowedSourceBiomeMask))
            {
                continue;
            }

            float distance = Utils.DistanceXZ(runestonePosition, candidate.m_position);
            if (!found || distance < bestDistance)
            {
                bestDistance = distance;
                closest = candidate;
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetRunestoneGlobalPinLocationInstances(
        string locationName,
        out List<ZoneSystem.LocationInstance> instances)
    {
        instances = null!;
        string normalizedLocationName = (locationName ?? "").Trim();
        if (normalizedLocationName.Length == 0 ||
            !TryGetRunestoneGlobalPinLocationIndex(out RunestoneGlobalPinLocationIndex index))
        {
            return false;
        }

        return index.InstancesByName.TryGetValue(normalizedLocationName, out instances) &&
               instances.Count > 0;
    }

    private static bool TryGetRunestoneGlobalPinLocationIndex(out RunestoneGlobalPinLocationIndex index)
    {
        index = null!;
        ZoneSystem? zoneSystem = ZoneSystem.instance;
        if (zoneSystem == null)
        {
            RunestoneGlobalPinLocationIndexCache = null;
            return false;
        }

        int zoneSystemId = zoneSystem.GetInstanceID();
        int locationInstanceCount = zoneSystem.m_locationInstances.Count;
        if (RunestoneGlobalPinLocationIndexCache != null &&
            RunestoneGlobalPinLocationIndexCache.ZoneSystemId == zoneSystemId &&
            RunestoneGlobalPinLocationIndexCache.LocationInstanceCount == locationInstanceCount)
        {
            index = RunestoneGlobalPinLocationIndexCache;
            return true;
        }

        RunestoneGlobalPinLocationIndex rebuiltIndex = new()
        {
            ZoneSystemId = zoneSystemId,
            LocationInstanceCount = locationInstanceCount
        };

        foreach (ZoneSystem.LocationInstance locationInstance in zoneSystem.m_locationInstances.Values)
        {
            string candidateName = GetZoneLocationPrefabName(locationInstance.m_location);
            if (candidateName.Length == 0)
            {
                continue;
            }

            if (!rebuiltIndex.InstancesByName.TryGetValue(candidateName, out List<ZoneSystem.LocationInstance>? instances))
            {
                instances = new List<ZoneSystem.LocationInstance>();
                rebuiltIndex.InstancesByName[candidateName] = instances;
            }

            instances.Add(locationInstance);
        }

        RunestoneGlobalPinLocationIndexCache = rebuiltIndex;
        index = rebuiltIndex;
        return true;
    }

    private static Heightmap.Biome ResolveRunestoneGlobalPinSourceBiomeMask(LocationRunestoneGlobalPinTargetDefinition target)
    {
        if (target.SourceBiomes == null || target.SourceBiomes.Count == 0)
        {
            return Heightmap.Biome.None;
        }

        if (BiomeResolutionSupport.TryResolveBiomeMask(target.SourceBiomes, out Heightmap.Biome biomeMask))
        {
            return biomeMask;
        }

        WarnRunestoneGlobalPin(
            $"globalpin|invalid-source-biome|{target.LocationName}|{string.Join(",", target.SourceBiomes)}",
            $"Runestone global pin target '{target.LocationName}' has invalid sourceBiomes value. Matching-biome RuneStones can still target it.");
        return Heightmap.Biome.None;
    }

    private static bool RunestoneGlobalPinBiomesMatch(Heightmap.Biome targetBiome, Heightmap.Biome runestoneBiome)
    {
        return targetBiome == runestoneBiome ||
               (targetBiome != Heightmap.Biome.None &&
                runestoneBiome != Heightmap.Biome.None &&
                (targetBiome & runestoneBiome) != 0);
    }

    private static bool RunestoneGlobalPinAllowsSourceBiome(Heightmap.Biome runestoneBiome, Heightmap.Biome allowedSourceBiomeMask)
    {
        return allowedSourceBiomeMask == Heightmap.Biome.All ||
               (allowedSourceBiomeMask != Heightmap.Biome.None &&
                runestoneBiome != Heightmap.Biome.None &&
                (allowedSourceBiomeMask & runestoneBiome) != 0);
    }

    private static Heightmap.Biome GetRunestoneGlobalPinBiome(Vector3 position)
    {
        return WorldGenerator.instance != null
            ? WorldGenerator.instance.GetBiome(position)
            : Heightmap.FindBiome(position);
    }

    private static string GetRunestoneGlobalPinDefaultPinName(string locationName)
    {
        string configuredName = GetRunestoneGlobalPinConfiguredDefaultName(locationName);
        return configuredName.Length > 0 ? configuredName : locationName;
    }

    private static string GetRunestoneGlobalPinConfiguredDefaultName(string locationName)
    {
        if (RunestoneGlobalPinDefaultPinNames.TryGetValue(locationName, out string? cached))
        {
            return cached;
        }

        string label = "";
        if (ZoneSystem.instance != null)
        {
            foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
            {
                string candidateName = GetZoneLocationPrefabName(location);
                if (!string.Equals(candidateName, locationName, StringComparison.OrdinalIgnoreCase) ||
                    !location.m_prefab.IsValid)
                {
                    continue;
                }

                location.m_prefab.Load();
                try
                {
                    label = GetRunestoneGlobalPinConfiguredDefaultName(location.m_prefab.Asset);
                }
                finally
                {
                    location.m_prefab.Release();
                }

                break;
            }
        }

        RunestoneGlobalPinDefaultPinNames[locationName] = label;
        return label;
    }

    private static string GetRunestoneGlobalPinConfiguredDefaultName(GameObject? locationPrefab)
    {
        if (locationPrefab == null)
        {
            return "";
        }

        string discoverLabel = (locationPrefab.GetComponent<Location>()?.m_discoverLabel ?? "").Trim();
        if (discoverLabel.Length > 0)
        {
            return discoverLabel;
        }

        foreach (Teleport teleport in locationPrefab.GetComponentsInChildren<Teleport>(includeInactive: true))
        {
            string enterText = (teleport?.m_enterText ?? "").Trim();
            if (enterText.Length > 0)
            {
                return enterText;
            }
        }

        return "";
    }

    private static Minimap.PinType ParseRunestoneGlobalPinType(string? rawPinType, string locationName)
    {
        string pinTypeName = string.IsNullOrWhiteSpace(rawPinType) ? Minimap.PinType.Icon3.ToString() : rawPinType!.Trim();
        if (Enum.TryParse(pinTypeName, ignoreCase: true, out Minimap.PinType pinType))
        {
            return pinType;
        }

        WarnRunestoneGlobalPin(
            $"globalpin|invalid-pintype|{locationName}|{pinTypeName}",
            $"Runestone global pin target '{locationName}' has invalid pinType '{pinTypeName}'. Using Icon3.");
        return Minimap.PinType.Icon3;
    }

    private static bool HasSimilarRunestoneGlobalPin(Vector3 position, Minimap.PinType pinType, string pinName, bool save)
    {
        if (Minimap.instance == null ||
            MinimapPinsField?.GetValue(Minimap.instance) is not List<Minimap.PinData> pins)
        {
            return false;
        }

        foreach (Minimap.PinData pin in pins)
        {
            if (pin.m_type == pinType &&
                pin.m_save == save &&
                string.Equals(pin.m_name, pinName, StringComparison.Ordinal) &&
                Utils.DistanceXZ(pin.m_pos, position) < 1f)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryRequestServerResolvedRunestoneGlobalPin(RuneStone runestone, string? runestoneLocationName)
    {
        EnsureRunestoneGlobalPinRpcRegistered();
        if (ZRoutedRpc.instance == null ||
            ZNet.instance == null ||
            ZNet.instance.IsServer())
        {
            return false;
        }

        ZPackage package = new();
        package.Write(RunestoneGlobalPinRpcVersion);
        WriteRunestoneGlobalPinVector3(package, runestone.transform.position);
        package.Write(runestoneLocationName ?? runestone.m_locationName ?? "");
        ZRoutedRpc.instance.InvokeRoutedRPC(0L, RunestoneGlobalPinRequestRpc, package);
        return true;
    }

    private static void OnRunestoneGlobalPinRequestRpc(long sender, ZPackage package)
    {
        if (ZRoutedRpc.instance == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !TryReadRunestoneGlobalPinRequest(package, out Vector3 runestonePosition, out string runestoneLocationName))
        {
            return;
        }

        ResolvedRunestoneGlobalPin? pin = ResolveServerRunestoneGlobalPin(runestonePosition, runestoneLocationName);
        ZPackage response = new();
        WriteRunestoneGlobalPinResponse(response, pin);
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, RunestoneGlobalPinResponseRpc, response);
    }

    private static void OnRunestoneGlobalPinResponseRpc(long sender, ZPackage package)
    {
        if (!IsRunestoneGlobalPinServerRoutedSender(sender) ||
            !TryReadRunestoneGlobalPinResponse(package, out ResolvedRunestoneGlobalPin? pin) ||
            pin == null)
        {
            return;
        }

        TryAddRunestoneGlobalPin(pin);
    }

    private static ResolvedRunestoneGlobalPin? ResolveServerRunestoneGlobalPin(Vector3 runestonePosition, string? runestoneLocationName)
    {
        if (!PluginSettingsFacade.IsLocationDomainEnabled() ||
            !PluginSettingsFacade.IsRunestoneGlobalPinsEnabled() ||
            !string.IsNullOrWhiteSpace(runestoneLocationName))
        {
            return null;
        }

        LocationRunestoneGlobalPinsDefinition? definition = GetEffectiveRunestoneGlobalPinsDefinition();
        if (definition?.TargetLocations == null || definition.TargetLocations.Count == 0)
        {
            return null;
        }

        return GetOrRollServerRunestoneGlobalPin(runestonePosition, definition, runestoneLocationName);
    }

    private static bool TryReadRunestoneGlobalPinRequest(
        ZPackage package,
        out Vector3 runestonePosition,
        out string runestoneLocationName)
    {
        runestonePosition = default;
        runestoneLocationName = "";
        try
        {
            int version = package.ReadInt();
            if (version != RunestoneGlobalPinRpcVersion)
            {
                return false;
            }

            runestonePosition = ReadRunestoneGlobalPinVector3(package);
            runestoneLocationName = package.ReadString();
            return true;
        }
        catch (Exception ex)
        {
            WarnRunestoneGlobalPin(
                "globalpin|rpc-request-invalid",
                $"Received invalid runestone global pin request RPC. Ignoring it. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void WriteRunestoneGlobalPinResponse(ZPackage package, ResolvedRunestoneGlobalPin? pin)
    {
        package.Write(RunestoneGlobalPinRpcVersion);
        package.Write(pin != null);
        if (pin == null)
        {
            return;
        }

        package.Write(pin.LocationName ?? "");
        package.Write(pin.PinName ?? "");
        package.Write((int)pin.PinType);
        WriteRunestoneGlobalPinVector3(package, pin.Position);
    }

    private static bool TryReadRunestoneGlobalPinResponse(ZPackage package, out ResolvedRunestoneGlobalPin? pin)
    {
        pin = null;
        try
        {
            int version = package.ReadInt();
            if (version != RunestoneGlobalPinRpcVersion || !package.ReadBool())
            {
                return true;
            }

            pin = new ResolvedRunestoneGlobalPin
            {
                LocationName = package.ReadString(),
                PinName = package.ReadString(),
                PinType = (Minimap.PinType)package.ReadInt(),
                Position = ReadRunestoneGlobalPinVector3(package)
            };
            return true;
        }
        catch (Exception ex)
        {
            WarnRunestoneGlobalPin(
                "globalpin|rpc-response-invalid",
                $"Received invalid runestone global pin response RPC. Ignoring it. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void WriteRunestoneGlobalPinVector3(ZPackage package, Vector3 value)
    {
        package.Write(value.x);
        package.Write(value.y);
        package.Write(value.z);
    }

    private static Vector3 ReadRunestoneGlobalPinVector3(ZPackage package)
    {
        return new Vector3(package.ReadSingle(), package.ReadSingle(), package.ReadSingle());
    }

    private static bool IsRunestoneGlobalPinServerRoutedSender(long sender)
    {
        if (ZRoutedRpc.instance == null || ZNet.instance == null)
        {
            return false;
        }

        if (ZNet.instance.IsServer())
        {
            return RunestoneGlobalPinRoutedRpcIdRef(ZRoutedRpc.instance) == sender;
        }

        ZNetPeer? serverPeer = ZNet.instance.GetServerPeer();
        return serverPeer != null && serverPeer.m_uid == sender;
    }

    private static string CreateRunestoneGlobalPinsRollKey(
        RuneStone runestone,
        LocationRunestoneGlobalPinsDefinition definition,
        string? originalLocationName)
    {
        StringBuilder builder = new();
        builder.Append(runestone.GetInstanceID().ToString(CultureInfo.InvariantCulture)).Append('\n');
        Vector3 position = runestone.transform.position;
        builder.Append(position.x.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(position.y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(position.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append(originalLocationName ?? runestone.m_locationName ?? "").Append('\n');
        AppendRunestoneGlobalPinsDefinitionRollKey(builder, definition);

        return builder.ToString();
    }

    private static string CreateRunestoneGlobalPinsServerRollKey(
        Vector3 runestonePosition,
        LocationRunestoneGlobalPinsDefinition definition,
        string? runestoneLocationName)
    {
        StringBuilder builder = new();
        builder.Append(runestonePosition.x.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(runestonePosition.y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(runestonePosition.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append(runestoneLocationName ?? "").Append('\n');
        AppendRunestoneGlobalPinsDefinitionRollKey(builder, definition);

        return builder.ToString();
    }

    private static void AppendRunestoneGlobalPinsDefinitionRollKey(
        StringBuilder builder,
        LocationRunestoneGlobalPinsDefinition definition)
    {
        if (definition.TargetLocations != null)
        {
            foreach (LocationRunestoneGlobalPinTargetDefinition target in definition.TargetLocations)
            {
                builder.Append(target.LocationName).Append('|')
                    .Append(target.Chance?.ToString("R", CultureInfo.InvariantCulture) ?? "").Append('|')
                    .Append(target.SourceBiomes == null ? "" : string.Join(",", target.SourceBiomes)).Append('|')
                    .Append(target.PinName ?? "").Append('|')
                    .Append(target.PinType ?? "").Append('\n');
            }
        }
    }

    private static void WarnRunestoneGlobalPin(string warningKey, string message)
    {
        if (!RunestoneGlobalPinWarningLogs.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);
    }
}
