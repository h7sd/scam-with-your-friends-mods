using System;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScamWYF.AiBackend;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine.UIElements;

namespace ScamWYF.ElevenLabsAgents
{
    [BepInPlugin(PluginGuid, "ElevenLabs Agents", "1.0.1")]
    [BepInDependency(ScamWYF.AiBackend.Plugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
    public sealed class Plugin : ScamMod
    {
        public const string PluginGuid = "com.community.scamwyf.elevenlabsagents";
        internal static Plugin Current;
        internal ConfigEntry<bool> BridgeEnabled;
        internal ConfigEntry<string> BridgeUrl;
        internal ConfigEntry<int> TimeoutSeconds;
        internal ConfigEntry<bool> VoiceEnabled;
        internal ConfigEntry<bool> RecognitionEnabled;
        private AgentsBackend backend;
        internal int ChatRequests;
        internal int VoiceRequests;
        internal int RecognitionRequests;
        internal int Failures;
        internal string LastError;
        internal string LastOutcome;
        internal string LastCaller;
        internal bool BridgeReachable;
        internal bool AgentConfigured;
        internal bool VoiceConfigured;

        protected override void OnModLoad()
        {
            Current = this;
            BridgeEnabled = Config.Bind("Bridge", "Enabled", true,
                "Route game AI requests through the local ElevenLabs Agents bridge. Enabled failures never fall back to the hosted AI.");
            BridgeUrl = Config.Bind("Bridge", "BaseUrl", "http://127.0.0.1:8765",
                "Local bridge root URL. Keys and model are configured in bridge/.env, never in this game config.");
            TimeoutSeconds = Config.Bind("Bridge", "TimeoutSeconds", 45,
                "Maximum seconds for one chat, recognition or speech request (5..600).");
            VoiceEnabled = Config.Bind("Voice", "Enabled", true,
                "Replace the game's caller/narration speech with ElevenLabs via the bridge. Requires Bridge.Enabled.");
            RecognitionEnabled = Config.Bind("Recognition", "Enabled", true,
                "Recognize completed microphone utterances through ElevenLabs Agents, using the game's VAD and resampling.");
            WatchConfig();
            backend = new AgentsBackend(this);
            var guard = new HarmonyMethod(AccessTools.Method(typeof(Plugin), "FallbackGuardPrefix"));
            guard.priority = Priority.First;
            guard.before = new[] { ScamWYF.AiBackend.Plugin.PluginGuid };
            if (!PatchCoordinator.TryPatch(this, typeof(KolkataApi), "CompleteOpenRouterAsync",
                new[] { typeof(JObject), typeof(CancellationToken), typeof(bool) },
                "keep enabled Agents routing active when the dependency router is in Passthrough", prefix: guard))
                throw new InvalidOperationException("Could not install the fail-closed Agents backend guard.");
            VoiceRouter.Install(this);
            RecognitionRouter.Install(this);
            CallerContext.Install(this);
            MenuStatus.Install(this);
            ModMenu.AddPage(this, "ElevenLabs Agents", BuildPage, -30);
            // Register only after all required hooks exist. ScamMod calls OnModUnload and removes
            // this owner's patches if any later load step fails.
            AiBackendApi.Register(backend);
            StartCoroutine(BridgeClient.PollHealth(this));
            ModLog.LogInfo("ElevenLabs Agents backend registered. ElevenLabs uses the game's existing PCM frame/networking path.");
        }

        protected override void OnModUnload()
        {
            if (backend != null) AiBackendApi.Unregister(backend);
            VoiceRouter.Shutdown();
            RecognitionRouter.Shutdown();
            CallerContext.Shutdown();
            if (Current == this) Current = null;
        }

        protected override void OnConfigReloaded()
        {
            if (!IsVoiceEnabled) VoiceRouter.CancelAll();
            ModMenu.Refresh();
        }

        internal bool IsVoiceEnabled { get { return BridgeEnabled.Value && VoiceEnabled.Value; } }
        internal int RequestTimeout { get { return Math.Max(5, Math.Min(600, TimeoutSeconds.Value)); } }

        internal string Endpoint(string path)
        {
            Uri root;
            if (!Uri.TryCreate((BridgeUrl.Value ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out root)
                || root.Scheme != "http" || !root.IsLoopback || !string.IsNullOrEmpty(root.UserInfo)
                || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
                throw new InvalidOperationException("Bridge.BaseUrl must be an HTTP loopback URL such as http://127.0.0.1:8765.");
            return new Uri(root, path.TrimStart('/')).AbsoluteUri;
        }

        internal void Failed(string message)
        {
            Failures++;
            LastError = message;
            ModLog.LogError(message);
        }

        private static bool FallbackGuardPrefix(JObject body, CancellationToken cancellationToken,
            System.Reflection.MethodBase __originalMethod, ref UniTask<JObject> __result)
        {
            var plugin = Current;
            if (plugin == null || plugin.backend == null || !plugin.BridgeEnabled.Value) return true;
            var patches = Harmony.GetPatchInfo(__originalMethod);
            if (patches != null)
            {
                foreach (var prefix in patches.Prefixes)
                {
                    if (prefix.owner == ScamWYF.AiBackend.Plugin.PluginGuid) return true;
                }
            }
            // The dependency can remove its own prefix after a config reload into Passthrough.
            // Keep the game off its hosted route while this backend is explicitly enabled.
            return !plugin.backend.TryComplete(body, cancellationToken, out __result);
        }

        private void BuildPage(VisualElement page)
        {
            Widgets.Heading(page, "ElevenLabs Agents");
            Widgets.FieldRow(page, "AI backend", BridgeEnabled.Value ? "ElevenLabs Agents bridge" : "Disabled");
            Widgets.FieldRow(page, "Voice", IsVoiceEnabled ? "ElevenLabs" : "Game speech provider");
            Widgets.FieldRow(page, "Speech recognition", BridgeEnabled.Value && RecognitionEnabled.Value ? "ElevenLabs Agents" : "Game recognizer");
            Widgets.FieldRow(page, "Bridge", BridgeUrl.Value);
            Widgets.FieldRow(page, "Connection", BridgeReachable ? "Reachable" : "Unavailable / not started");
            Widgets.FieldRow(page, "Agent configuration", AgentConfigured ? "Ready" : "Open the bridge setup page");
            Widgets.FieldRow(page, "Voice configuration", VoiceConfigured ? "Ready" : "Open the bridge setup page");
            Widgets.Note(page, "The game waits for a complete structured agent reply. ElevenLabs audio uses the existing call audio and multiplayer path. Speech recognition, replies and voice are provided by your configured ElevenLabs Agent.");
            Widgets.Heading(page, "This session");
            Widgets.FieldRow(page, "Chat requests", ChatRequests.ToString());
            Widgets.FieldRow(page, "Voice requests", VoiceRequests.ToString());
            Widgets.FieldRow(page, "Recognition requests", RecognitionRequests.ToString());
            Widgets.FieldRow(page, "Failures", Failures.ToString());
            if (!string.IsNullOrEmpty(LastOutcome)) Widgets.FieldRow(page, "Last result", LastOutcome);
            if (!string.IsNullOrEmpty(LastCaller)) Widgets.FieldRow(page, "Last caller", LastCaller);
            if (!string.IsNullOrEmpty(LastError)) Widgets.Note(page, LastError, true, true);
            Widgets.Note(page, "Configure your ElevenLabs Agent ID and API credentials in the bridge's .env file. No provider keys are sent to or stored by the game mod.");
            Widgets.DescribedButton(Widgets.WrapRow(page), "Reload config", "Apply changes from the config file", ReloadConfig);
            ConfigEditor.Build(page);
        }
    }

    internal sealed class AgentsBackend : IChatBackend
    {
        private readonly Plugin plugin;
        internal AgentsBackend(Plugin plugin) { this.plugin = plugin; }
        public string Name { get { return "ElevenLabs Agents"; } }

        public bool TryComplete(JObject request, CancellationToken cancellationToken, out UniTask<JObject> response)
        {
            if (!plugin.BridgeEnabled.Value)
            {
                response = default(UniTask<JObject>);
                return false;
            }
            // Accept before any potentially failing work. The upstream router's synchronous fallback
            // catch must never run after an enabled request has been assigned to this backend.
            var completion = new UniTaskCompletionSource<JObject>();
            response = completion.Task;
            plugin.ChatRequests++;
            try { plugin.StartCoroutine(BridgeClient.Chat(plugin, CallerContext.EnrichChat(request), cancellationToken, completion)); }
            catch (Exception ex)
            {
                plugin.Failed("ElevenLabs Agents bridge could not start: " + ex.Message);
                completion.TrySetException(ex);
            }
            return true;
        }
    }
}
