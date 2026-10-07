using System;
using System.Reflection;
using HarmonyLib;
using ScamWYF.Modding.Core;
using UnityEngine.UIElements;

namespace ScamWYF.ElevenLabsAgents
{
    internal static class MenuStatus
    {
        private static FieldInfo backendLabel;
        private static FieldInfo warningTitle;
        private static FieldInfo warningBody;
        private static MethodInfo setStatusColor;

        internal static void Install(Plugin plugin)
        {
            var type = AccessTools.TypeByName("MainMenuConnectionStatus");
            var refresh = type == null ? null : AccessTools.Method(type, "Refresh");
            if (refresh == null)
            {
                plugin.ModLog.LogWarning("The main menu AI label hook is unavailable in this game build.");
                return;
            }
            backendLabel = AccessTools.Field(type, "backendLabel");
            warningTitle = AccessTools.Field(type, "warningTitle");
            warningBody = AccessTools.Field(type, "warningBody");
            setStatusColor = AccessTools.Method(type, "SetStatusColor", new[] { typeof(Label), typeof(bool), typeof(bool) });
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(MenuStatus), "RefreshPostfix"));
            postfix.priority = Priority.Last;
            postfix.after = new[] { ScamWYF.AiBackend.Plugin.PluginGuid };
            PatchCoordinator.TryPatch(plugin, refresh, "show ElevenLabs Agents in the AI backend label", postfix: postfix);
        }

        private static void RefreshPostfix(object __instance, bool aiCreditsDepleted)
        {
            var plugin = Plugin.Current;
            if (plugin == null || !plugin.BridgeEnabled.Value || backendLabel == null) return;
            try
            {
                var label = backendLabel.GetValue(__instance) as Label;
                if (label != null)
                {
                    label.text = plugin.IsVoiceEnabled ? "AI BACKEND: ELEVENLABS AGENTS" : "AI BACKEND: ELEVENLABS AGENTS";
                    bool ready = plugin.BridgeReachable && plugin.AgentConfigured;
                    if (setStatusColor != null) setStatusColor.Invoke(null, new object[] { label, ready, !ready });
                }
                if (!aiCreditsDepleted) return;
                var title = warningTitle == null ? null : warningTitle.GetValue(__instance) as Label;
                var body = warningBody == null ? null : warningBody.GetValue(__instance) as Label;
                if (title != null) title.text = "ELEVENLABS AGENTS";
                if (body != null) body.text = "AI uses the local ElevenLabs Agents bridge at " + plugin.BridgeUrl.Value
                    + ". Voice uses " + (plugin.IsVoiceEnabled ? "ElevenLabs" : "the game speech provider")
                    + ". Configure provider keys in bridge/.env and open the F1 mod tab for request diagnostics.";
            }
            catch (Exception) { /* UI diagnostics must not interrupt the game menu. */ }
        }
    }
}
