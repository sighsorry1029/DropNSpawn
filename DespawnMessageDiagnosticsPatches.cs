using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(MessageHud), "RPC_ShowMessage")]
internal static class MessageHudRpcShowMessageDespawnDiagnosticsPatch
{
    private static void Prefix(long sender, int type, string text)
    {
        if (!PluginSettingsFacade.IsDespawnDiagnosticsEnabled() ||
            !DespawnRulesManager.IsManagedDespawnMessage(text))
        {
            return;
        }

        string localPlayerName = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "";
        DespawnRulesManager.LogDiagnostics(
            $"Client received ShowMessage RPC. localPeerId={ZNet.GetUID()} localPlayer='{localPlayerName}' sender={sender} type={(MessageHud.MessageType)type} text='{text}'.");
    }
}

[HarmonyPatch(typeof(MessageHud), nameof(MessageHud.ShowMessage))]
internal static class MessageHudShowMessageDespawnDiagnosticsPatch
{
    private static void Prefix(MessageHud.MessageType type, string text, int amount, Sprite icon, bool showDespiteHiddenHUD)
    {
        if (!PluginSettingsFacade.IsDespawnDiagnosticsEnabled() ||
            !DespawnRulesManager.IsManagedDespawnMessage(text))
        {
            return;
        }

        string localPlayerName = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "";
        DespawnRulesManager.LogDiagnostics(
            $"Client queued HUD message. localPeerId={ZNet.GetUID()} localPlayer='{localPlayerName}' type={type} amount={amount} hiddenHudOverride={showDespiteHiddenHUD} text='{text}'.");
    }
}
