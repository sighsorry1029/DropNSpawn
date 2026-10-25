using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DropNSpawn.Tests
{
    // Scene-free registration and reload-boundary fixtures; production parsing, splitting,
    // validation/commit orchestration, DTOs and timer identities execute unchanged.
    internal static class SpawnIntervalProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static bool ready;
        private static Heightmap.Biome customBit = (Heightmap.Biome)(1 << 20);
        private static bool Skip() => false;
        private static bool Yes(ref bool __result) { __result = true; return false; }
        private static bool Ready(ref bool __result) { __result = ready; return false; }
        private static bool Resolve(string configuredBiome, ref Heightmap.Biome biome, ref bool __result)
        {
            __result = ready && configuredBiome == "FixtureCustomBiome";
            biome = __result ? customBit : Heightmap.Biome.None;
            return false;
        }
        private static bool Display(Heightmap.Biome biome, ref string displayName, ref bool __result)
        {
            __result = ready && biome == customBit;
            displayName = __result ? "FixtureCustomBiome" : "";
            return false;
        }
        private static bool Known(ref Heightmap.Biome __result) { __result = Heightmap.Biome.All | customBit; return false; }
        private static bool SceneReady(object[] __args, ref bool __result)
        { __result = __args[0].GetType().Name != "WaitingForIntervalBiomes"; return false; }
        private static bool ValidationKey(ref string __result) { __result = "interval-fixture:" + ready; return false; }
        private static bool Compile(ref bool __result) { __result = true; return false; }
        private static object Read(object target, string property) => target.GetType().GetProperty(property, All).GetValue(target);

        internal static int Run(Assembly mod)
        {
            int count = 0;
            void Check(bool condition, string message)
            { if (!condition) throw new InvalidOperationException(message); count++; }
            Type manager = mod.GetType("DropNSpawn.SpawnSystemManager", true);
            Type biomes = mod.GetType("DropNSpawn.BiomeResolutionSupport", true);
            Type plugin = mod.GetType("DropNSpawn.DropNSpawnPlugin", true);
            Type settings = mod.GetType("DropNSpawn.PluginSettingsFacade", true);
            Type timers = mod.GetType("DropNSpawn.SpawnSystemTimers", true);
            object runtime = manager.GetField("ConfigurationRuntime", All).GetValue(null);
            object load = Read(runtime, "LoadState");
            object state = manager.GetField("RuntimeState", All).GetValue(null);
            var restore = new List<Action>();
            foreach (object target in new[] { load, state })
                foreach (PropertyInfo property in target.GetType().GetProperties(All).Where(p => p.CanWrite))
                {
                    object previous = property.GetValue(target);
                    restore.Add(() => property.SetValue(target, previous));
                }
            var fixture = new Harmony("DropNSpawn.isolated.spawn.intervals");
            string folder = Path.Combine(Path.GetTempPath(), "dns-intervals-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string rows = Path.Combine(folder, "DNS_spawnsystem.yml"), policy = Path.Combine(folder, "DNS_spawnintervals.yml");
            try
            {
                void Patch(Type type, string method, string prefix) => fixture.Patch(type.GetMethod(method, All),
                    prefix: new HarmonyMethod(typeof(SpawnIntervalProbe), prefix));
                Patch(biomes, "IsExpandWorldDataPresent", nameof(Yes));
                Patch(biomes, "IsExpandWorldDataReadyOrUnavailable", nameof(Ready));
                Patch(biomes, "TryResolveExpandWorldDataBiome", nameof(Resolve));
                Patch(biomes, "TryGetExpandWorldDataBiomeDisplayName", nameof(Display));
                Patch(biomes, "GetKnownBiomeMask", nameof(Known));
                Patch(manager, "CanStrictlyValidateLocalConfigurationNow", nameof(SceneReady));
                Patch(manager, "BuildStrictLocalValidationEnvironmentKey", nameof(ValidationKey));
                Patch(manager, "ApplyEntry", nameof(Compile));
                Patch(manager, "PublishSyncedConfigurationOrDeferLocked", nameof(Skip));
                Patch(plugin, "get_IsSourceOfTruth", nameof(Yes));
                Patch(settings, "ShouldDisableLowTierBiomeGlobalKeySpawnSystemEntries", nameof(Yes));
                object Reload(bool includePolicy = true) => runtime.GetType().GetMethod("ReloadSourceOfTruth", All)
                    .Invoke(runtime, new object[] { includePolicy ? new List<string> { rows, policy } : new List<string> { rows } });
                IList Entries() => (IList)Read(state, "Configuration");
                Array Ids() => (Array)timers.GetMethod("CreateIds", All).Invoke(null, new[] { Read(state, "Configuration") });
                object Spawn(object row) => Read(row, "SpawnSystem");
                File.WriteAllText(rows, "- prefab: Wolf\n  spawnSystem:\n    spawnInterval: 60\n    biomes: [Meadows, DeepNorth]\n    requiredGlobalKey: fixture\n");
                File.WriteAllText(policy, "FixtureCustomBiome: 2\nMeadows: 0.5");
                ready = false;
                object before = Read(state, "Configuration");
                Check(Reload().ToString() == "DeferredStrictValidation" && ReferenceEquals(before, Read(state, "Configuration")),
                    "policy-only custom name defers before committing unscaled rows");
                ready = true;
                Check(Reload().ToString() == "Loaded" && Entries().Count == 2, "same pending payload retries after biome registration");
                object high = Entries().Cast<object>().Single(e => ((List<string>)Read(Spawn(e), "Biomes")).Contains("DeepNorth"));
                var highData = new SpawnSystem.SpawnData { m_biome = Heightmap.Biome.DeepNorth, m_requiredGlobalKey = "fixture" };
                Check((bool)manager.GetMethod("ShouldSkipLowTierBiomeGlobalKeySpawnSystemEntry", All)
                    .Invoke(null, new[] { (object)highData, Read(high, "LowTierGlobalKeySource") }),
                    "high-biome child still obeys original mixed-row global-key filter");
                object accepted = Read(state, "Configuration"), active = manager.GetField("_activeCompiledTable", All).GetValue(null);
                string payload = (string)Read(load, "LastLoadedPayload");
                foreach (string invalid in new[] { "Meadows: 0", "UnknownBiome: 2", "Meadows: .inf" })
                {
                    File.WriteAllText(policy, invalid);
                    Check(Reload().ToString() == "Rejected" && ReferenceEquals(accepted, Read(state, "Configuration")) &&
                        (string)Read(load, "LastLoadedPayload") == payload && ReferenceEquals(active, manager.GetField("_activeCompiledTable", All).GetValue(null)),
                        "invalid policy preserves accepted payload, configuration and active table");
                }
                Check(Reload(false).ToString() == "Loaded" && Entries().Count == 1 && (float)Read(Spawn(Entries()[0]), "SpawnInterval") == 60,
                    "policy removal restores base intervals on actual reload");
                File.WriteAllText(policy, "Meadows: 0.5");
                Check(Reload().ToString() == "Loaded" && Entries().Count == 2, "policy-only file change reapplies without editing spawn YAML");
                accepted = Read(state, "Configuration");
                File.WriteAllText(rows, "- spawnIntervalMultiplier: 2\n- prefab: Wolf\n");
                Check(Reload().ToString() == "Rejected" && ReferenceEquals(accepted, Read(state, "Configuration")), "removed header rejects at the real commit boundary");
                File.WriteAllText(rows, "- prefab: Wolf\n  spawnSystem:\n    spawnInterval: 60\n    biomes: [All]\n");
                File.WriteAllText(policy, "FixtureCustomBiome: 2");
                Check(Reload().ToString() == "Loaded" && Entries().Count == 2, "All with a custom-biome factor creates two groups");
                Array firstIds = Ids();
                Check(((List<string>)Read(Spawn(Entries()[1]), "Biomes")).Single() == "FixtureCustomBiome", "custom child stores its name, not a numeric enum allocation");
                customBit = (Heightmap.Biome)(1 << 21);
                load.GetType().GetProperty("PendingStrictPayload", All).SetValue(load, Read(load, "LastLoadedPayload"));
                Check(Reload().ToString() == "Loaded" && Ids().Cast<object>().SequenceEqual(firstIds.Cast<object>()),
                    "EWD-ready reparse updates masks without changing name-based timer IDs when a custom bit moves");
                Heightmap.Biome firstMask = (Heightmap.Biome)Read(Spawn(Entries()[0]), "ResolvedBiomeMask");
                Heightmap.Biome secondMask = (Heightmap.Biome)Read(Spawn(Entries()[1]), "ResolvedBiomeMask");
                Check((firstMask & secondMask) == 0 && secondMask == customBit, "strict validation never widens the vanilla group into the custom group");
            }
            finally
            {
                for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
                fixture.UnpatchSelf();
                File.Delete(rows); File.Delete(policy); Directory.Delete(folder);
            }
            return count;
        }
    }
}
