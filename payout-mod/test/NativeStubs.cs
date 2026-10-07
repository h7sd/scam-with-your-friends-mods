using System;
using System.Reflection;
using HarmonyLib;

// The harness exercises the production hooks against real native game methods.
// Only the Unity plugin lifecycle/config wrapper is replaced inside this test process.
namespace ScamWYF.RequestedPayout
{
    internal sealed class TestEnabled { internal bool Value = true; }
    internal sealed class Plugin
    {
        internal static Plugin Current;
        internal TestEnabled PayoutEnabled = new TestEnabled();
        internal int AcceptedPrices, RequestedPayouts, LastAmount;
        internal string LastStatus;
    }
}

namespace ScamWYF.Modding.Core
{
    internal static class PatchCoordinator
    {
        private static readonly Harmony harmony = new Harmony("scamwyf.requestedpayout.independent-native-tests");
        internal static bool TryPatch(ScamWYF.RequestedPayout.Plugin owner, Type type, string method,
            Type[] signature, string purpose, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null)
        {
            MethodBase target = method == ".ctor" ? (MethodBase)AccessTools.Constructor(type, signature)
                : AccessTools.Method(type, method, signature);
            harmony.Patch(target, prefix, postfix, transpiler);
            return true;
        }
        internal static bool TryPatch(ScamWYF.RequestedPayout.Plugin owner, MethodBase method,
            string purpose, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null)
        {
            harmony.Patch(method, prefix, postfix, transpiler);
            return true;
        }
    }
}
