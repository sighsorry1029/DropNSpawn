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

}
