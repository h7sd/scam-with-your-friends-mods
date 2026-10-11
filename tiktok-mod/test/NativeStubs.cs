using System;
using System.Reflection;
using HarmonyLib;

namespace ScamWYF.TikTokPocket
{
    // Only the Unity plugin lifecycle is replaced. Production catalog/browser hooks are linked.
    internal sealed class TestSetting { internal bool Value = true; }
    internal sealed class Plugin
    {
        internal const string AppId = "tiktok-pocket", ProductId = "tiktok-pocket-license";
        internal static Plugin Current;
        internal int ShopPrice = 1000;
        internal string IconPath, ProfilePath, Status;
        internal TestSetting RememberSession = new TestSetting();
        internal void SetStatus(string status) { Status = status; }
        internal void ReportCompatibility(string status) {Status=status;}
    }
}

namespace ScamWYF.Modding.Core.Ui
{
    internal static class ModMenu {internal static void Refresh() {}}
}

namespace ScamWYF.Modding.Core
{
    internal static class PatchCoordinator
    {
        private static readonly Harmony harmony = new Harmony("scamwyf.tiktokpocket.independent-native-tests");
        internal static bool TryPatch(ScamWYF.TikTokPocket.Plugin owner, MethodBase method, string purpose,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null)
        {
            // BuildDesktop contains Unity UI default-interface implementations unsupported by
            // Windows CLR4. Its actual production prefix is invoked on storage-only state.
            if (method.Name == "BuildDesktop") return true;
            harmony.Patch(method, prefix, postfix, transpiler);
            return true;
        }
    }
}
