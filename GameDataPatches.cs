using HarmonyLib;

namespace DropNSpawn;

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class ZNetSceneAwakePatch
{
    private static void Postfix()
    {
        BossRulesManager.ClearRuntimeState();
        DespawnRulesManager.MarkBootstrapScanDirty("ZNetScene.Awake");
        DropNSpawnPlugin.QueueGameDataRefresh(DropNSpawnPlugin.ReloadDomain.All, "ZNetScene.Awake");
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class ObjectDBAwakePatch
{
    private static void Postfix()
    {
        DropNSpawnPlugin.QueueGameDataRefresh(DropNSpawnPlugin.ReloadDomain.All, "ObjectDB.Awake");
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class ObjectDBCopyOtherDBPatch
{
    private static void Postfix()
    {
        DropNSpawnPlugin.QueueGameDataRefresh(DropNSpawnPlugin.ReloadDomain.All, "ObjectDB.CopyOtherDB");
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Awake))]
internal static class ZoneSystemAwakePatch
{
    private static void Postfix()
    {
        DropNSpawnPlugin.QueueGameDataRefresh(DropNSpawnPlugin.ReloadDomain.Location, "ZoneSystem.Awake");
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
internal static class ZoneSystemStartPatch
{
    private static void Postfix()
    {
        DropNSpawnPlugin.QueueGameDataRefresh(DropNSpawnPlugin.ReloadDomain.Location | DropNSpawnPlugin.ReloadDomain.SpawnSystem, "ZoneSystem.Start");
    }
}
