using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

// 1.0.30 ZDO initializes a constant from Quaternion.identity.eulerAngles.
// Stand in for that single engine call only while running the real initializer.
internal static class HeadlessZdoFixture
{
    internal static void Initialize(Type zdo, MethodInfo conversion)
    {
        var harmony = new Harmony("DropNSpawn.tests.zdo.identity");
        try
        {
            harmony.Patch(conversion, transpiler: new HarmonyMethod(typeof(HeadlessZdoFixture), nameof(IdentityOnly)));
            RuntimeHelpers.RunClassConstructor(zdo.TypeHandle);
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static IEnumerable<CodeInstruction> IdentityOnly(MethodBase __originalMethod)
    {
        var method = (MethodInfo)__originalMethod;
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        if (!method.IsStatic) yield return new CodeInstruction(OpCodes.Ldobj, method.DeclaringType);
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HeadlessZdoFixture), nameof(ZeroAngles))
            .MakeGenericMethod(method.DeclaringType!, method.ReturnType));
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static TVector ZeroAngles<TQuaternion, TVector>(TQuaternion rotation) where TVector : struct
    {
        foreach (string field in new[] { "x", "y", "z", "w" })
            if ((float)typeof(TQuaternion).GetField(field)!.GetValue(rotation)! != (field == "w" ? 1f : 0f))
                throw new InvalidOperationException("Non-identity rotation requires a real Unity runtime.");
        return default;
    }
}
