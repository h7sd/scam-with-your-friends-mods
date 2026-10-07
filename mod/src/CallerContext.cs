using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using Newtonsoft.Json.Linq;
using ScamWYF.Modding.Core;

namespace ScamWYF.ElevenLabsAgents
{
    /// <summary>
    /// Bind the game's exact call/session identifiers to its caller metadata. Concurrent calls never
    /// share a global "current caller", and remote listeners use the game's synced public identity.
    /// </summary>
    internal static class CallerContext
    {
        private static readonly Dictionary<string, WeakReference> sessions = new Dictionary<string, WeakReference>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<uint, WeakReference> calls = new Dictionary<uint, WeakReference>();
        private static PropertyInfo selectedIdentity, callGuid, currentLanguage;
        private static FieldInfo voicePath, talker;
        private static Plugin plugin;

        internal static void Install(Plugin owner)
        {
            plugin = owner;
            selectedIdentity = AccessTools.Property(typeof(VictimCall), "SelectedCallerIdentity");
            callGuid = AccessTools.Property(typeof(NetworkedCall), "CallGuid");
            currentLanguage = AccessTools.Property(typeof(NetworkedCall), "ServerCurrentPromptLanguage");
            voicePath = AccessTools.Field(typeof(NetworkedCall), "voicePath");
            talker = AccessTools.Field(typeof(NetworkedCall), "talker");
            if (selectedIdentity == null || callGuid == null || currentLanguage == null || voicePath == null || talker == null)
                throw new InvalidOperationException("This game build has changed the caller identity/context fields.");
            if (!PatchCoordinator.TryPatch(owner, typeof(VictimCall), "EnsureAiPipeline", Type.EmptyTypes,
                "associate caller metadata with its exact AI session",
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CallerContext), "RegisterPostfix"))))
                throw new InvalidOperationException("Could not install caller identity/session hook.");
            if (!PatchCoordinator.TryPatch(owner, typeof(VictimCall), "ServerDisposeCall", Type.EmptyTypes,
                "release completed caller metadata",
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CallerContext), "ForgetPrefix"))))
                throw new InvalidOperationException("Could not install caller context cleanup hook.");
        }

        private static void RegisterPostfix(VictimCall __instance)
        {
            if (__instance == null) return;
            try
            {
                var session = Session(__instance);
                if (session.Length > 0) sessions[session] = new WeakReference(__instance);
                if (__instance.netId != 0) calls[__instance.netId] = new WeakReference(__instance);
            }
            catch (Exception ex) { if (plugin != null) plugin.Failed("Caller metadata registration failed: " + ex.Message); }
        }

        private static void ForgetPrefix(VictimCall __instance)
        {
            if (__instance == null) return;
            try
            {
                var session = Session(__instance);
                if (session.Length > 0) sessions.Remove(session);
                if (__instance.netId != 0) calls.Remove(__instance.netId);
            }
            catch (Exception) { /* Cleanup must not interrupt the game's call disposal. */ }
        }

        private static string Session(VictimCall call)
        {
            string value = ((Guid)callGuid.GetValue(call, null)).ToString("N");
            return value == "00000000000000000000000000000000" ? "" : value;
        }

        internal static JObject EnrichChat(JObject request)
        {
            if (request == null) return null;
            var result = (JObject)request.DeepClone();
            var key = NormalizeSession((string)request["session_id"]);
            WeakReference reference;
            if (key.Length == 0 || !sessions.TryGetValue(key, out reference)) return result;
            var call = reference.Target as VictimCall;
            if (call == null) { sessions.Remove(key); return result; }
            var context = Build(call, null, null);
            result["caller_context"] = context;
            result["call_id"] = key;
            result["call_net_id"] = call.netId;
            if (plugin != null) plugin.LastCaller = (string)context["caller_name"];
            return result;
        }

        internal static JObject ForSpeech(uint callId, string actualVoicePath, string responseLanguage)
        {
            var call = FindCall(callId);
            if (call != null) return Build(call, actualVoicePath, responseLanguage);
            // Narration and a caller whose network object has gone away retain explicit game voice
            // and response language. Do not attach the identity of a different live call.
            return new JObject { ["caller_id"] = "", ["caller_name"] = "", ["voice_path"] = actualVoicePath ?? "",
                ["caller_language_code"] = "", ["response_language_code"] = responseLanguage ?? "",
                ["player_language_code"] = PlayerLanguage(), ["call_net_id"] = callId };
        }

        private static VictimCall FindCall(uint id)
        {
            if (id == 0) return null;
            WeakReference reference;
            if (calls.TryGetValue(id, out reference))
            {
                var known = reference.Target as VictimCall;
                if (known != null) return known;
                calls.Remove(id);
            }
            NetworkIdentity identity;
            if (NetworkClient.spawned != null && NetworkClient.spawned.TryGetValue(id, out identity) && identity != null)
                return identity.GetComponent<VictimCall>();
            if (NetworkServer.spawned != null && NetworkServer.spawned.TryGetValue(id, out identity) && identity != null)
                return identity.GetComponent<VictimCall>();
            return null;
        }

        private static JObject Build(VictimCall call, string actualVoicePath, string responseLanguage)
        {
            var caller = selectedIdentity.GetValue(call, null) as CallerIdentity;
            JObject publicState = null;
            if (!string.IsNullOrEmpty(call.PublicUiStateJson))
            {
                try { publicState = JObject.Parse(call.PublicUiStateJson); } catch (Exception) { }
            }
            // SelectedCallerIdentity is host-side. The public state syncs the actual id to clients.
            string callerId = caller != null ? caller.id : publicState == null ? "" : (string)publicState["callerPersonalityId"] ?? "";
            if (caller == null && callerId.Length > 0) caller = CallerIdentityLibrary.GetForNetwork(callerId);
            var talkerInstance = talker.GetValue(call) as KolkataPlayerVoice;
            var context = new JObject {
                ["caller_id"] = callerId,
                ["caller_name"] = caller != null ? caller.displayName ?? "" : publicState == null ? "" : (string)publicState["callerPersonalityName"] ?? "",
                ["voice_path"] = actualVoicePath ?? (string)voicePath.GetValue(call) ?? (caller == null ? "" : caller.voiceId ?? ""),
                // The game's CallerIdentity constructor declares "en" when JSON omits the field.
                // Unresolved identities stay unknown; never derive a language from name/avatar.
                ["caller_language_code"] = caller == null ? "" : caller.languageCode ?? "",
                ["response_language_code"] = responseLanguage ?? (string)currentLanguage.GetValue(call, null) ?? "",
                ["player_language_code"] = PlayerLanguage(talkerInstance),
                ["call_net_id"] = call.netId,
                ["call_id"] = Session(call),
            };
            string gender = caller != null ? caller.gender : publicState == null ? null : (string)publicState["callerGender"];
            if (!string.IsNullOrEmpty(gender)) context["gender"] = gender;
            if (publicState != null && publicState["callerAge"] != null && publicState["callerAge"].Type == JTokenType.Integer)
            {
                int age = (int)publicState["callerAge"];
                if (age > 0 && age <= 150) context["age"] = age;
            }
            if (caller != null && !string.IsNullOrEmpty(caller.personalityPrompt))
                context["voice_description"] = caller.personalityPrompt.Length > 5000 ? caller.personalityPrompt.Substring(0, 5000) : caller.personalityPrompt;
            return context;
        }

        internal static string PlayerLanguage(KolkataPlayerVoice player = null)
        {
            if (player == null) player = KolkataPlayerVoice.LocalPlayerInstance;
            if (player != null && !string.IsNullOrEmpty(player.CallLanguageCode)) return player.CallLanguageCode;
            // This is the game's selected speech language, independent from the caller's profile.
            var languageType = AccessTools.TypeByName("Kolkata.Speech.CallLanguage");
            var current = languageType == null ? null : AccessTools.Property(languageType, "Current");
            var option = current == null ? null : current.GetValue(null, null);
            var code = option == null ? null : AccessTools.Field(option.GetType(), "Code");
            return code == null ? "" : (string)code.GetValue(option) ?? "";
        }

        internal static string NormalizeSession(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Trim().Replace("-", "").ToLowerInvariant();
        }

        internal static void Shutdown()
        {
            sessions.Clear(); calls.Clear(); plugin = null;
        }
    }
}
