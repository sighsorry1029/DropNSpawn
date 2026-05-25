using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static class BossStonePerPlayerRuntime
{
    private const string StartTemplePrefabName = "StartTemple";
    private const string DeepNorthBossStonesPrefabName = "DeepNorth_BossStones_TW";
    private const string PlayerKeyPrefix = "dns_bossstone_";
    private const string BossStoneSacrificeRequestRpc = "DropNSpawn BossStone Sacrifice Request";
    private const string BossStoneResetRequestRpc = "DropNSpawn BossStone Reset Request";
    private const string BossStoneResetApplyRpc = "DropNSpawn BossStone Reset Apply";
    private const string BossStoneResetAckRpc = "DropNSpawn BossStone Reset Ack";
    private const string BossStoneResetStatusRpc = "DropNSpawn BossStone Reset Status";
    private const float BossStoneResetRetryIntervalSeconds = 0.5f;
    private const float BossStoneResetRequestTimeoutSeconds = 10f;
    private static readonly HashSet<string> PerPlayerBossStoneLocationPrefabs = new(StringComparer.OrdinalIgnoreCase)
    {
        StartTemplePrefabName,
        DeepNorthBossStonesPrefabName
    };

    private sealed class PendingBossStoneResetRequest
    {
        public long RequestId { get; set; }
        public long RequesterPeerId { get; set; }
        public string TargetPlayerName { get; set; } = "";
        public float CreatedAt { get; set; }
        public float NextRetryAt { get; set; }
    }

    private static readonly AccessTools.FieldRef<ItemStand, ZNetView> ItemStandNviewRef =
        AccessTools.FieldRefAccess<ItemStand, ZNetView>("m_nview");
    private static readonly AccessTools.FieldRef<ZRoutedRpc, long> RoutedRpcIdRef =
        AccessTools.FieldRefAccess<ZRoutedRpc, long>("m_id");

    private static readonly MethodInfo? ItemStandGetOrientationMethod =
        AccessTools.Method(typeof(ItemStand), "GetOrientation");

    private static readonly MethodInfo? ItemStandSetVisualItemMethod =
        AccessTools.Method(typeof(ItemStand), "SetVisualItem", new[] { typeof(string), typeof(int), typeof(int), typeof(int) });

    private static ZRoutedRpc? _registeredRpcInstance;
    private static readonly Dictionary<long, PendingBossStoneResetRequest> PendingBossStoneResetRequests = new();
    private static long _nextBossStoneSacrificeRequestId = 1L;
    private static long _nextBossStoneResetRequestId = 1L;

    internal static void Initialize()
    {
        EnsureRpcRegistered();
    }

    private static void LogDiagnostic(string message)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo("[BossStone] " + message);
    }

    internal static void EnsureRpcRegistered()
    {
        ZRoutedRpc? rpc = ZRoutedRpc.instance;
        if (rpc == null || ReferenceEquals(rpc, _registeredRpcInstance))
        {
            return;
        }

        if (_registeredRpcInstance != null)
        {
            Shutdown();
        }

        rpc.Register<long, string, Vector3>(BossStoneSacrificeRequestRpc, OnBossStoneSacrificeRequestRpc);
        rpc.Register<string>(BossStoneResetRequestRpc, OnBossStoneResetRequestRpc);
        rpc.Register<long>(BossStoneResetApplyRpc, OnBossStoneResetApplyRpc);
        rpc.Register<long, int>(BossStoneResetAckRpc, OnBossStoneResetAckRpc);
        rpc.Register<string>(BossStoneResetStatusRpc, OnBossStoneResetStatusRpc);
        _registeredRpcInstance = rpc;
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic("Registered boss stone routed RPC handlers.");
        }
    }

    internal static void Shutdown()
    {
        _registeredRpcInstance = null;
        PendingBossStoneResetRequests.Clear();
    }

    internal static bool ShouldHandle(ItemStand? itemStand)
    {
        return PluginSettingsFacade.IsPerPlayerBossStonesEnabled() &&
               TryGetBossStone(itemStand, out BossStone? bossStone) &&
               IsPerPlayerBossStone(bossStone);
    }

    internal static bool TryRequestReset(string exactPlayerName, out string message)
    {
        if (!TryResolveKnownPlayerName(exactPlayerName, out string resolvedPlayerName))
        {
            string normalizedPlayerName = (exactPlayerName ?? "").Trim();
            message = normalizedPlayerName.Length == 0
                ? "Syntax: dns:bossstone reset <exactPlayerName>"
                : $"Player '{normalizedPlayerName}' not found. Use exact player name.";
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Reset request rejected before send. input='{normalizedPlayerName}' reason='player_not_found'.");
            }
            return false;
        }

        EnsureRpcRegistered();
        if (ZRoutedRpc.instance == null)
        {
            message = "Boss stone reset is unavailable because routed RPC is not ready.";
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Reset request rejected before send. target='{resolvedPlayerName}' reason='rpc_not_ready'.");
            }
            return false;
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(BossStoneResetRequestRpc, resolvedPlayerName);
        message = $"Queued boss stone reset request for '{resolvedPlayerName}'. Awaiting server result.";
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Reset request sent. target='{resolvedPlayerName}'.");
        }
        return true;
    }

    internal static void ProcessPendingResetRequests()
    {
        if (ZRoutedRpc.instance == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            PendingBossStoneResetRequests.Count == 0)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        long[] requestIds = PendingBossStoneResetRequests.Keys.ToArray();
        foreach (long requestId in requestIds)
        {
            if (!PendingBossStoneResetRequests.TryGetValue(requestId, out PendingBossStoneResetRequest? request) ||
                request == null ||
                now < request.NextRetryAt)
            {
                continue;
            }

            if (now - request.CreatedAt >= BossStoneResetRequestTimeoutSeconds)
            {
                if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
                {
                    LogDiagnostic($"Reset request timed out. requestId={requestId} target='{request.TargetPlayerName}'.");
                }
                CompletePendingBossStoneResetRequest(
                    requestId,
                    success: false,
                    $"Boss stone reset failed for '{request.TargetPlayerName}': target did not acknowledge within {BossStoneResetRequestTimeoutSeconds:0.#}s.");
                continue;
            }

            if (TryGetHostedLocalPlayerName(out string hostedLocalPlayerName) &&
                string.Equals(request.TargetPlayerName, hostedLocalPlayerName, StringComparison.Ordinal))
            {
                if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
                {
                    LogDiagnostic($"Reset apply routed locally for hosted player. requestId={requestId} target='{request.TargetPlayerName}'.");
                }
                ZRoutedRpc.instance.InvokeRoutedRPC(BossStoneResetApplyRpc, request.RequestId);
                request.NextRetryAt = now + BossStoneResetRetryIntervalSeconds;
                continue;
            }

            ZNetPeer? targetPeer = ZNet.instance.GetPeerByPlayerName(request.TargetPlayerName);
            if (targetPeer == null || !targetPeer.IsReady())
            {
                if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
                {
                    LogDiagnostic($"Reset request waiting for ready peer. requestId={requestId} target='{request.TargetPlayerName}' peerFound={(targetPeer != null)} peerReady={(targetPeer?.IsReady() ?? false)}.");
                }
                request.NextRetryAt = now + BossStoneResetRetryIntervalSeconds;
                continue;
            }

            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Reset apply sent. requestId={requestId} target='{request.TargetPlayerName}' peerId={targetPeer.m_uid}.");
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(targetPeer.m_uid, BossStoneResetApplyRpc, request.RequestId);
            request.NextRetryAt = now + BossStoneResetRetryIntervalSeconds;
        }
    }

    internal static bool TryInspectCurrentTarget(out string[] lines, out string error)
    {
        error = "";
        Player? localPlayer = Player.m_localPlayer;
        if (localPlayer == null)
        {
            lines = Array.Empty<string>();
            error = "Local player is not ready.";
            return false;
        }

        GameObject? hoverObject = localPlayer.GetHoverObject();
        if (hoverObject == null)
        {
            lines = Array.Empty<string>();
            error = "No hover target.";
            return false;
        }

        ItemStand? itemStand = hoverObject.GetComponent<ItemStand>() ?? hoverObject.GetComponentInParent<ItemStand>();
        if (!TryGetBossStone(itemStand, out BossStone? bossStone) || bossStone == null)
        {
            lines = Array.Empty<string>();
            error = "Current hover target is not a boss stone item stand.";
            return false;
        }

        Location? location = GetBossStoneLocation(bossStone);
        string locationPrefabName = location != null ? Utils.GetPrefabName(location.gameObject.name) : "<none>";
        string playerKey = GetPlayerKey(bossStone);
        lines =
        [
            $"BossStone: yes",
            $"PerPlayerEligible: {IsPerPlayerBossStone(bossStone)}",
            $"Location: {locationPrefabName}",
            $"GuardianPower: {bossStone.m_itemStand?.m_guardianPower?.name ?? "<none>"}",
            $"PlayerKey: {playerKey}",
            $"ActiveForLocalPlayer: {LocalPlayerHasStoneActive(itemStand)}"
        ];
        return true;
    }

    internal static bool TryHandleUseItem(ItemStand itemStand, Humanoid user, ItemDrop.ItemData item, out bool result)
    {
        result = false;

        if (!ShouldHandle(itemStand) ||
            Player.m_localPlayer == null ||
            user is not Player localPlayer ||
            localPlayer != Player.m_localPlayer)
        {
            return false;
        }

        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic(
                $"Sacrifice use-item intercepted. player='{localPlayer.GetPlayerName()}' item='{item?.m_dropPrefab?.name ?? item?.m_shared?.m_name ?? "<null>"}' " +
                $"bossStone='{itemStand.m_guardianPower?.name ?? "<none>"}' canAccept={(item != null && CanAcceptConfiguredSacrifice(itemStand, item))}.");
        }

        if (item == null || !CanAcceptConfiguredSacrifice(itemStand, item) || !localPlayer.GetInventory().ContainsItem(item))
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic(
                    $"Sacrifice use-item rejected locally. player='{localPlayer.GetPlayerName()}' itemPresent={(item != null)} " +
                    $"canAccept={(item != null && CanAcceptConfiguredSacrifice(itemStand, item))} inventoryContains={(item != null && localPlayer.GetInventory().ContainsItem(item))}.");
            }
            localPlayer.Message(MessageHud.MessageType.Center, "$piece_itemstand_cantattach");
            result = true;
            return true;
        }

        if (!TryGetBossStone(itemStand, out BossStone? bossStone) || bossStone == null)
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Sacrifice use-item fell through because boss stone resolution failed after local validation. player='{localPlayer.GetPlayerName()}'.");
            }
            return false;
        }

        long requestId = _nextBossStoneSacrificeRequestId++;
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic(
                $"Sacrifice applying locally before broadcast. requestId={requestId} player='{localPlayer.GetPlayerName()}' " +
                $"item='{item.m_dropPrefab?.name ?? item.m_shared?.m_name ?? "<null>"}'.");
        }

        if (!TryApplyLocalBossStoneSacrifice(localPlayer, bossStone, item, requestId))
        {
            result = true;
            return true;
        }

        if (!TryBroadcastSacrifice(bossStone, requestId))
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Sacrifice broadcast skipped after local apply. requestId={requestId} player='{localPlayer.GetPlayerName()}'.");
            }
        }

        result = true;
        return true;
    }

    internal static bool TryOverrideHaveAttachment(ItemStand itemStand, out bool result)
    {
        result = false;
        if (!ShouldHandle(itemStand))
        {
            return false;
        }

        result = LocalPlayerHasStoneActive(itemStand);
        return true;
    }

    internal static bool TryOverrideUpdateVisual(ItemStand itemStand)
    {
        if (!ShouldHandle(itemStand))
        {
            return false;
        }

        string itemName = LocalPlayerHasStoneActive(itemStand)
            ? itemStand.m_supportedItems?.FirstOrDefault()?.name ?? ""
            : "";
        int orientation = GetOrientation(itemStand);
        SetVisualItem(itemStand, itemName, 0, 1, orientation);
        return true;
    }

    internal static int ClearBossStonePlayerKeys(Player player)
    {
        if (player == null)
        {
            return 0;
        }

        List<string> keysToRemove = player.GetUniqueKeys()
            .Where(key => key != null && key.StartsWith(PlayerKeyPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (string key in keysToRemove)
        {
            player.RemoveUniqueKey(key);
        }

        if (keysToRemove.Count > 0 && player == Player.m_localPlayer)
        {
            ForsakenPowerSelectionRuntime.InvalidateGuardianPowerCache();
        }

        return keysToRemove.Count;
    }

    internal static bool LocalPlayerHasStoneActive(ItemStand? itemStand)
    {
        if (!TryGetBossStone(itemStand, out BossStone? bossStone) || bossStone == null || Player.m_localPlayer == null)
        {
            return false;
        }

        return Player.m_localPlayer.HaveUniqueKey(GetPlayerKey(bossStone));
    }

    internal static bool HasUnlockedGuardianPower(Player? player, string guardianPowerName)
    {
        if (player == null || string.IsNullOrWhiteSpace(guardianPowerName))
        {
            return false;
        }

        return player.HaveUniqueKey(PlayerKeyPrefix + guardianPowerName.Trim());
    }

    internal static List<string> GetUnlockedGuardianPowerNames(Player? player)
    {
        if (player == null)
        {
            return new List<string>();
        }

        return player.GetUniqueKeys()
            .Where(key => key != null && key.StartsWith(PlayerKeyPrefix, StringComparison.Ordinal))
            .Select(key => key.Substring(PlayerKeyPrefix.Length))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool CanAcceptConfiguredSacrifice(ItemStand itemStand, ItemDrop.ItemData item)
    {
        if (itemStand == null || item == null)
        {
            return false;
        }

        if (!itemStand.CanAttach(item))
        {
            return false;
        }

        List<ItemDrop>? supportedItems = itemStand.m_supportedItems;
        if (supportedItems == null || supportedItems.Count == 0)
        {
            return true;
        }

        string candidateSharedName = item.m_shared?.m_name ?? "";
        string candidatePrefabName = item.m_dropPrefab?.name ?? "";
        foreach (ItemDrop supportedItem in supportedItems)
        {
            if (supportedItem == null)
            {
                continue;
            }

            string supportedSharedName = supportedItem.m_itemData?.m_shared?.m_name ?? "";
            string supportedPrefabName = supportedItem.gameObject != null ? supportedItem.gameObject.name : supportedItem.name;
            if ((candidateSharedName.Length > 0 &&
                 string.Equals(candidateSharedName, supportedSharedName, StringComparison.OrdinalIgnoreCase)) ||
                (candidatePrefabName.Length > 0 &&
                 string.Equals(candidatePrefabName, supportedPrefabName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CanAcceptConfiguredSacrifice(ItemStand itemStand, string itemPrefabName)
    {
        if (itemStand == null || string.IsNullOrWhiteSpace(itemPrefabName))
        {
            return false;
        }

        List<ItemDrop>? supportedItems = itemStand.m_supportedItems;
        if (supportedItems == null || supportedItems.Count == 0)
        {
            return false;
        }

        string normalizedItemPrefabName = itemPrefabName.Trim();
        foreach (ItemDrop supportedItem in supportedItems)
        {
            if (supportedItem == null)
            {
                continue;
            }

            string supportedPrefabName = supportedItem.gameObject != null ? supportedItem.gameObject.name : supportedItem.name;
            if (string.Equals(normalizedItemPrefabName, supportedPrefabName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryApplyLocalBossStoneSacrifice(Player localPlayer, BossStone bossStone, ItemDrop.ItemData item, long requestId)
    {
        if (!TryConsumeLocalBossStoneSacrifice(localPlayer, item))
        {
            localPlayer.Message(MessageHud.MessageType.Center, "$piece_itemstand_cantattach");
            return false;
        }

        string playerKey = GetPlayerKey(bossStone);
        if (!localPlayer.HaveUniqueKey(playerKey))
        {
            localPlayer.AddUniqueKey(playerKey);
            ForsakenPowerSelectionRuntime.InvalidateGuardianPowerCache();
        }

        RefreshAllBossStoneVisuals();
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Sacrifice applied locally. requestId={requestId} player='{localPlayer.GetPlayerName()}' key='{playerKey}'.");
        }

        return true;
    }

    private static bool TryResolveKnownPlayerName(string playerName, out string resolvedPlayerName)
    {
        resolvedPlayerName = "";
        string normalizedPlayerName = (playerName ?? "").Trim();
        if (normalizedPlayerName.Length == 0)
        {
            return false;
        }

        if (Player.m_localPlayer != null &&
            string.Equals(Player.m_localPlayer.GetPlayerName(), normalizedPlayerName, StringComparison.OrdinalIgnoreCase))
        {
            resolvedPlayerName = Player.m_localPlayer.GetPlayerName();
            return true;
        }

        foreach (ZNet.PlayerInfo playerInfo in ZNet.instance?.GetPlayerList() ?? new List<ZNet.PlayerInfo>())
        {
            string candidateName = (playerInfo.m_name ?? "").Trim();
            if (string.Equals(candidateName, normalizedPlayerName, StringComparison.OrdinalIgnoreCase))
            {
                resolvedPlayerName = candidateName;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetBossStone(ItemStand? itemStand, out BossStone? bossStone)
    {
        bossStone = null;
        if (itemStand == null || itemStand.m_guardianPower == null)
        {
            return false;
        }

        bossStone = itemStand.GetComponentInParent<BossStone>();
        return bossStone != null;
    }

    private static Location? GetBossStoneLocation(BossStone? bossStone)
    {
        return bossStone == null ? null : Location.GetLocation(bossStone.transform.position, true);
    }

    private static bool IsPerPlayerBossStone(BossStone? bossStone)
    {
        Location? location = GetBossStoneLocation(bossStone);
        return location != null &&
               PerPlayerBossStoneLocationPrefabs.Contains(Utils.GetPrefabName(location.gameObject.name));
    }

    private static string GetPlayerKey(BossStone bossStone)
    {
        string guardianPowerName = TryResolveBossStoneGuardianPowerName(bossStone, out string resolvedGuardianPowerName)
            ? resolvedGuardianPowerName
            : Utils.GetPrefabName(bossStone.gameObject.name);
        return PlayerKeyPrefix + guardianPowerName;
    }

    private static bool TryResolveBossStoneGuardianPowerName(BossStone? bossStone, out string guardianPowerName)
    {
        guardianPowerName = bossStone?.m_itemStand?.m_guardianPower?.name?.Trim() ?? "";
        if (guardianPowerName.Length > 0)
        {
            return true;
        }

        string prefabName = bossStone?.gameObject != null ? Utils.GetPrefabName(bossStone.gameObject.name).Trim() : "";
        if (prefabName.Length == 0)
        {
            return false;
        }

        if (prefabName.StartsWith("GP_", StringComparison.Ordinal))
        {
            guardianPowerName = prefabName;
            return true;
        }

        const string bossStonePrefix = "BossStone_";
        if (prefabName.StartsWith(bossStonePrefix, StringComparison.OrdinalIgnoreCase) &&
            prefabName.Length > bossStonePrefix.Length)
        {
            guardianPowerName = "GP_" + prefabName.Substring(bossStonePrefix.Length);
            return true;
        }

        return false;
    }

    private static bool IsLocalPlayerInsideTemple(BossStone bossStone)
    {
        Player? localPlayer = Player.m_localPlayer;
        Location? location = GetBossStoneLocation(bossStone);
        return localPlayer != null &&
               location != null &&
               location.IsInside(localPlayer.transform.position, 0f, false);
    }

    private static bool TryBroadcastSacrifice(BossStone bossStone, long requestId)
    {
        EnsureRpcRegistered();
        ZNetView? nview = GetItemStandZNetView(bossStone.m_itemStand);
        if (ZRoutedRpc.instance == null)
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic(
                    $"Sacrifice broadcast aborted. bossStone='{bossStone.m_itemStand?.m_guardianPower?.name ?? "<none>"}' " +
                    $"routedRpcReady={(ZRoutedRpc.instance != null)}.");
            }
            return false;
        }

        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic(
                $"Sacrifice request broadcast. requestId={requestId} itemStandId={(nview?.GetZDO()?.m_uid ?? ZDOID.None)} requestView='{nview?.name ?? "<none>"}' bossStone='{bossStone.m_itemStand?.m_guardianPower?.name ?? "<none>"}'.");
        }
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, BossStoneSacrificeRequestRpc, requestId, GetPlayerKey(bossStone), bossStone.transform.position);
        return true;
    }

    private static ZNetView? GetItemStandZNetView(ItemStand? itemStand)
    {
        if (itemStand == null)
        {
            return null;
        }

        return itemStand.m_netViewOverride != null
            ? itemStand.m_netViewOverride
            : ItemStandNviewRef(itemStand) ?? itemStand.GetComponent<ZNetView>();
    }

    private static void OnBossStoneSacrificeRequestRpc(long sender, long requestId, string playerKey, Vector3 bossStonePosition)
    {
        bool validKey = TryNormalizePlayerKey(playerKey, out string normalizedPlayerKey);
        Player? localPlayer = Player.m_localPlayer;
        Location? location = Location.GetLocation(bossStonePosition, true);
        bool insideLocation = localPlayer != null &&
                              location != null &&
                              location.IsInside(localPlayer.transform.position, 0f, false);
        bool senderIsLocal = IsLocalRoutedSender(sender);
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic(
                $"Sacrifice broadcast received. requestId={requestId} sender={sender} senderIsLocal={senderIsLocal} validKey={validKey} " +
                $"location='{(location != null ? Utils.GetPrefabName(location.gameObject.name) : "<none>")}' localPlayer='{localPlayer?.GetPlayerName() ?? "<null>"}' insideLocation={insideLocation}.");
        }

        if (!validKey || localPlayer == null || location == null || !insideLocation)
        {
            return;
        }

        if (!localPlayer.HaveUniqueKey(normalizedPlayerKey))
        {
            localPlayer.AddUniqueKey(normalizedPlayerKey);
            ForsakenPowerSelectionRuntime.InvalidateGuardianPowerCache();
        }

        RefreshAllBossStoneVisuals();
    }

    private static void OnBossStoneResetRequestRpc(long sender, string exactPlayerName)
    {
        if (ZRoutedRpc.instance == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer())
        {
            return;
        }

        if (!TryResolveKnownPlayerName(exactPlayerName, out string resolvedPlayerName))
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Reset request received by server but target not found. sender={sender} input='{(exactPlayerName ?? "").Trim()}'.");
            }
            SendBossStoneResetStatus(sender, $"Boss stone reset failed: player '{exactPlayerName?.Trim()}' was not found.");
            return;
        }

        long requestId = _nextBossStoneResetRequestId++;
        PendingBossStoneResetRequests[requestId] = new PendingBossStoneResetRequest
        {
            RequestId = requestId,
            RequesterPeerId = sender,
            TargetPlayerName = resolvedPlayerName,
            CreatedAt = Time.realtimeSinceStartup,
            NextRetryAt = Time.realtimeSinceStartup
        };
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Reset request accepted by server. requestId={requestId} sender={sender} target='{resolvedPlayerName}'.");
        }
        ProcessPendingResetRequests();
    }

    private static void OnBossStoneResetApplyRpc(long sender, long requestId)
    {
        bool isServerSender = IsServerRoutedSender(sender);
        Player? localPlayer = Player.m_localPlayer;
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Reset apply received. requestId={requestId} sender={sender} serverValid={isServerSender} localPlayerReady={(localPlayer != null)} localPlayer='{localPlayer?.GetPlayerName() ?? "<null>"}'.");
        }
        if (!isServerSender)
        {
            return;
        }

        if (localPlayer == null)
        {
            return;
        }

        int removedCount = ClearBossStonePlayerKeys(localPlayer);
        RefreshAllBossStoneVisuals();
        Console.instance?.Print($"Removed {removedCount} boss stone keys from '{localPlayer.GetPlayerName()}'.");
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Reset apply completed locally. requestId={requestId} removedCount={removedCount} player='{localPlayer.GetPlayerName()}'.");
        }
        ZRoutedRpc.instance?.InvokeRoutedRPC(BossStoneResetAckRpc, requestId, removedCount);
    }

    private static void OnBossStoneResetAckRpc(long sender, long requestId, int removedCount)
    {
        if (ZRoutedRpc.instance == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !PendingBossStoneResetRequests.TryGetValue(requestId, out PendingBossStoneResetRequest? request) ||
            request == null)
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Reset ack ignored. sender={sender} requestId={requestId} pendingFound={PendingBossStoneResetRequests.ContainsKey(requestId)}.");
            }
            return;
        }

        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Reset ack received by server. sender={sender} requestId={requestId} removedCount={removedCount} target='{request.TargetPlayerName}'.");
        }
        CompletePendingBossStoneResetRequest(
            requestId,
            success: true,
            $"Boss stone reset completed for '{request.TargetPlayerName}': removed {removedCount} key(s).");
    }

    private static void OnBossStoneResetStatusRpc(long sender, string message)
    {
        bool isServerSender = IsServerRoutedSender(sender);
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Reset status received. sender={sender} serverValid={isServerSender} message='{message}'.");
        }
        if (!isServerSender)
        {
            return;
        }

        Console.instance?.Print(message);
    }


    private static bool TryGetHostedLocalPlayerName(out string hostedLocalPlayerName)
    {
        hostedLocalPlayerName = "";
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            Player.m_localPlayer == null)
        {
            return false;
        }

        hostedLocalPlayerName = Player.m_localPlayer.GetPlayerName()?.Trim() ?? "";
        return hostedLocalPlayerName.Length > 0;
    }

    private static bool TryGetHostedLocalPlayerPeerId(out long hostedLocalPeerId)
    {
        hostedLocalPeerId = 0L;
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            Player.m_localPlayer == null ||
            ZRoutedRpc.instance == null)
        {
            return false;
        }

        hostedLocalPeerId = RoutedRpcIdRef(ZRoutedRpc.instance);
        return hostedLocalPeerId != 0L;
    }

    private static bool IsLocalRoutedSender(long sender)
    {
        return ZRoutedRpc.instance != null && RoutedRpcIdRef(ZRoutedRpc.instance) == sender;
    }

    private static bool TryNormalizePlayerKey(string playerKey, out string normalizedPlayerKey)
    {
        normalizedPlayerKey = (playerKey ?? "").Trim();
        return normalizedPlayerKey.StartsWith(PlayerKeyPrefix, StringComparison.Ordinal) &&
               normalizedPlayerKey.Length > PlayerKeyPrefix.Length;
    }

    private static bool TryConsumeLocalBossStoneSacrifice(Player localPlayer, ItemDrop.ItemData item)
    {
        if (localPlayer == null || item == null)
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic("Sacrifice consume could not remove item because local player or item was not ready.");
            }
            return false;
        }

        bool removed = false;
        string itemPrefabName = item.m_dropPrefab?.name ?? "";
        string itemName = item.m_shared?.m_name ?? itemPrefabName;
        if (localPlayer.GetInventory().ContainsItem(item))
        {
            localPlayer.UnequipItem(item, triggerEquipEffects: false);
            removed = localPlayer.GetInventory().RemoveOneItem(item);
            if (removed)
            {
                localPlayer.ShowRemovedMessage(item, 1);
            }
        }
        else if (itemName.Length > 0 && localPlayer.GetInventory().CountItems(itemName) > 0)
        {
            localPlayer.GetInventory().RemoveItem(itemName, 1);
            removed = true;
        }

        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic(
                $"Sacrifice consume finalized local inventory. player='{localPlayer.GetPlayerName()}' item='{itemName}' prefab='{itemPrefabName}' removed={removed}.");
        }

        return removed;
    }

    private static void CompletePendingBossStoneResetRequest(long requestId, bool success, string message)
    {
        if (!PendingBossStoneResetRequests.TryGetValue(requestId, out PendingBossStoneResetRequest? request) ||
            request == null)
        {
            return;
        }

        PendingBossStoneResetRequests.Remove(requestId);
        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Reset request completed on server. requestId={requestId} success={success} requester={request.RequesterPeerId} target='{request.TargetPlayerName}' message='{message}'.");
        }
        SendBossStoneResetStatus(request.RequesterPeerId, message);
    }

    private static void SendBossStoneResetStatus(long requesterPeerId, string message)
    {
        if (ZRoutedRpc.instance == null)
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Reset status printed locally because routed RPC is unavailable. requester={requesterPeerId} message='{message}'.");
            }
            Console.instance?.Print(message);
            return;
        }

        if (requesterPeerId == 0L)
        {
            if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
            {
                LogDiagnostic($"Reset status printed locally for local requester. message='{message}'.");
            }
            Console.instance?.Print(message);
            return;
        }

        if (PluginSettingsFacade.IsBossStoneDiagnosticsEnabled())
        {
            LogDiagnostic($"Reset status sent to requester. requester={requesterPeerId} message='{message}'.");
        }
        ZRoutedRpc.instance.InvokeRoutedRPC(requesterPeerId, BossStoneResetStatusRpc, message);
    }

    private static bool IsServerRoutedSender(long sender)
    {
        if (ZRoutedRpc.instance == null || ZNet.instance == null)
        {
            return false;
        }

        if (ZNet.instance.IsServer())
        {
            return RoutedRpcIdRef(ZRoutedRpc.instance) == sender;
        }

        ZNetPeer? serverPeer = ZNet.instance.GetServerPeer();
        return serverPeer != null && serverPeer.m_uid == sender;
    }

    private static int GetOrientation(ItemStand itemStand)
    {
        object? orientation = ItemStandGetOrientationMethod?.Invoke(itemStand, null);
        return orientation is int value ? value : 0;
    }

    private static void SetVisualItem(ItemStand itemStand, string itemName, int variant, int quality, int orientation)
    {
        ItemStandSetVisualItemMethod?.Invoke(itemStand, new object[] { itemName, variant, quality, orientation });
    }

    private static void RefreshAllBossStoneVisuals()
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        foreach (BossStone bossStone in UnityEngine.Object.FindObjectsByType<BossStone>(FindObjectsSortMode.None))
        {
            if (bossStone != null && IsPerPlayerBossStone(bossStone))
            {
                TryOverrideUpdateVisual(bossStone.m_itemStand);
            }
        }
    }
}
