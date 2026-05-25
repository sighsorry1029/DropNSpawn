using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static class SceneProximityQueries
{
    internal static int CountPlayersInRangeXZ(Vector3 point, float range, bool livingPlayersOnly)
    {
        if (range <= 0f)
        {
            return 0;
        }

        if (DropNSpawnPlugin.IsRuntimeServer())
        {
            return CountServerPlayersInRangeXZ(point, range * range, livingPlayersOnly);
        }

        int count = 0;
        float rangeSquared = range * range;
        foreach (Player player in Player.GetAllPlayers())
        {
            if (!IsPlayerInRangeXZ(player, point, rangeSquared, livingPlayersOnly))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    internal static int CountLivingPlayersInRangeXZ(Vector3 point, float range)
    {
        return CountPlayersInRangeXZ(point, range, livingPlayersOnly: true);
    }

    internal static bool AnyLivingPlayerInRangeXZ(Vector3 point, float range)
    {
        return TryFindAnyLivingPlayerInRangeXZ(point, range, out _);
    }

    internal static bool TryFindAnyLivingPlayerInRangeXZ(Vector3 point, float range, out long playerId)
    {
        playerId = 0L;
        if (range <= 0f)
        {
            return false;
        }

        float rangeSquared = range * range;
        if (DropNSpawnPlugin.IsRuntimeServer())
        {
            return TryFindAnyServerPlayerInRangeXZ(point, rangeSquared, out playerId);
        }

        foreach (Player player in Player.GetAllPlayers())
        {
            if (player == null ||
                player.gameObject == null ||
                player.IsDead())
            {
                continue;
            }

            long candidatePlayerId = player.GetPlayerID();
            if (candidatePlayerId == 0L || !IsWithinRangeXZ(player.transform.position, point, rangeSquared))
            {
                continue;
            }

            playerId = candidatePlayerId;
            return true;
        }

        return false;
    }

    internal static bool TryFindNearestLivingPlayerInRangeXZ(Vector3 point, float range, out long playerId)
    {
        playerId = 0L;
        if (range <= 0f)
        {
            return false;
        }

        float rangeSquared = range * range;
        if (DropNSpawnPlugin.IsRuntimeServer())
        {
            return TryFindNearestServerPlayerInRangeXZ(point, rangeSquared, out playerId);
        }

        float bestDistanceSquared = float.MaxValue;
        foreach (Player player in Player.GetAllPlayers())
        {
            if (player == null ||
                player.gameObject == null ||
                player.IsDead())
            {
                continue;
            }

            long candidatePlayerId = player.GetPlayerID();
            if (candidatePlayerId == 0L)
            {
                continue;
            }

            Vector3 offset = player.transform.position - point;
            offset.y = 0f;
            float distanceSquared = offset.sqrMagnitude;
            if (distanceSquared >= rangeSquared || distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            playerId = candidatePlayerId;
        }

        return playerId != 0L;
    }

    internal static void CollectLivingPlayerIdsInRangeXZ(Vector3 point, float range, HashSet<long> playerIds)
    {
        playerIds.Clear();
        if (range <= 0f)
        {
            return;
        }

        if (DropNSpawnPlugin.IsRuntimeServer())
        {
            CollectServerPlayerIdsInRangeXZ(point, range * range, playerIds);
            return;
        }

        float rangeSquared = range * range;
        foreach (Player player in Player.GetAllPlayers())
        {
            if (player == null ||
                player.gameObject == null ||
                player.IsDead())
            {
                continue;
            }

            long playerId = player.GetPlayerID();
            if (playerId == 0L || !IsWithinRangeXZ(player.transform.position, point, rangeSquared))
            {
                continue;
            }

            playerIds.Add(playerId);
        }
    }

    private static int CountServerPlayersInRangeXZ(Vector3 point, float rangeSquared, bool livingPlayersOnly)
    {
        int count = 0;
        if (IsLocalServerPlayerInRangeXZ(point, rangeSquared, livingPlayersOnly))
        {
            count++;
        }

        List<ZNetPeer>? peers = ZNet.instance?.GetPeers();
        if (peers == null)
        {
            return count;
        }

        foreach (ZNetPeer peer in peers)
        {
            if (IsServerPeerInRangeXZ(peer, point, rangeSquared, livingPlayersOnly))
            {
                count++;
            }
        }

        return count;
    }

    private static void CollectServerPlayerIdsInRangeXZ(Vector3 point, float rangeSquared, HashSet<long> playerIds)
    {
        long localPeerId = ZNet.GetUID();
        if (localPeerId != 0L &&
            IsLocalServerPlayerInRangeXZ(point, rangeSquared, livingPlayersOnly: true))
        {
            playerIds.Add(localPeerId);
        }

        List<ZNetPeer>? peers = ZNet.instance?.GetPeers();
        if (peers == null)
        {
            return;
        }

        foreach (ZNetPeer peer in peers)
        {
            if (peer != null &&
                peer.m_uid != 0L &&
                IsServerPeerInRangeXZ(peer, point, rangeSquared, livingPlayersOnly: true))
            {
                playerIds.Add(peer.m_uid);
            }
        }
    }

    private static bool TryFindAnyServerPlayerInRangeXZ(Vector3 point, float rangeSquared, out long playerId)
    {
        playerId = 0L;
        long localPeerId = ZNet.GetUID();
        if (localPeerId != 0L &&
            IsLocalServerPlayerInRangeXZ(point, rangeSquared, livingPlayersOnly: true))
        {
            playerId = localPeerId;
            return true;
        }

        List<ZNetPeer>? peers = ZNet.instance?.GetPeers();
        if (peers == null)
        {
            return false;
        }

        foreach (ZNetPeer peer in peers)
        {
            if (peer == null ||
                peer.m_uid == 0L ||
                !IsServerPeerInRangeXZ(peer, point, rangeSquared, livingPlayersOnly: true))
            {
                continue;
            }

            playerId = peer.m_uid;
            return true;
        }

        return false;
    }

    private static bool TryFindNearestServerPlayerInRangeXZ(Vector3 point, float rangeSquared, out long playerId)
    {
        playerId = 0L;
        float bestDistanceSquared = float.MaxValue;

        Player? localPlayer = Player.m_localPlayer;
        long localPeerId = ZNet.GetUID();
        if (localPeerId != 0L &&
            localPlayer != null &&
            localPlayer.gameObject != null &&
            !localPlayer.IsDead())
        {
            Vector3 localOffset = localPlayer.transform.position - point;
            localOffset.y = 0f;
            float localDistanceSquared = localOffset.sqrMagnitude;
            if (localDistanceSquared < rangeSquared)
            {
                bestDistanceSquared = localDistanceSquared;
                playerId = localPeerId;
            }
        }

        List<ZNetPeer>? peers = ZNet.instance?.GetPeers();
        if (peers == null)
        {
            return playerId != 0L;
        }

        foreach (ZNetPeer peer in peers)
        {
            if (peer == null ||
                peer.m_uid == 0L ||
                !IsServerPeerInRangeXZ(peer, point, rangeSquared, livingPlayersOnly: true))
            {
                continue;
            }

            Vector3 peerOffset = peer.GetRefPos() - point;
            peerOffset.y = 0f;
            float peerDistanceSquared = peerOffset.sqrMagnitude;
            if (peerDistanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = peerDistanceSquared;
            playerId = peer.m_uid;
        }

        return playerId != 0L;
    }

    private static bool IsPlayerInRangeXZ(Player? player, Vector3 point, float rangeSquared, bool livingPlayersOnly)
    {
        return player != null &&
               player.gameObject != null &&
               (!livingPlayersOnly || !player.IsDead()) &&
               IsWithinRangeXZ(player.transform.position, point, rangeSquared);
    }

    private static bool IsLocalServerPlayerInRangeXZ(Vector3 point, float rangeSquared, bool livingPlayersOnly)
    {
        Player? localPlayer = Player.m_localPlayer;
        return localPlayer != null &&
               localPlayer.gameObject != null &&
               (!livingPlayersOnly || !localPlayer.IsDead()) &&
               IsWithinRangeXZ(localPlayer.transform.position, point, rangeSquared);
    }

    private static bool IsServerPeerInRangeXZ(ZNetPeer? peer, Vector3 point, float rangeSquared, bool livingPlayersOnly)
    {
        if (peer == null ||
            !peer.IsReady() ||
            !IsWithinRangeXZ(peer.GetRefPos(), point, rangeSquared))
        {
            return false;
        }

        if (!livingPlayersOnly)
        {
            return true;
        }

        if (TryGetLoadedPeerPlayer(peer, out Player? player))
        {
            return player != null && !player.IsDead();
        }

        return true;
    }

    private static bool TryGetLoadedPeerPlayer(ZNetPeer peer, out Player? player)
    {
        player = null;
        if (peer == null ||
            peer.m_characterID.IsNone() ||
            ZNetScene.instance == null)
        {
            return false;
        }

        GameObject? instance = ZNetScene.instance.FindInstance(peer.m_characterID);
        return instance != null && instance.TryGetComponent(out player);
    }

    private static bool IsWithinRangeXZ(Vector3 source, Vector3 target, float rangeSquared)
    {
        Vector3 offset = source - target;
        offset.y = 0f;
        return offset.sqrMagnitude < rangeSquared;
    }

    private static string GetPrefabName(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return "";
        }

        ZNetView? nview = gameObject.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (zdo != null && ZNetScene.instance != null)
        {
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab != null)
            {
                return prefab.name;
            }
        }

        string prefabName = Utils.GetPrefabName(gameObject);
        if (!string.IsNullOrWhiteSpace(prefabName))
        {
            return prefabName;
        }

        const string cloneSuffix = "(Clone)";
        string name = gameObject.name ?? "";
        if (name.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            return name[..^cloneSuffix.Length].TrimEnd();
        }

        return name;
    }
}
