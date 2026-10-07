using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MetaVoiceChat.Core.NetEQ;
using Newtonsoft.Json.Linq;
using ScamWYF.Modding.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace ScamWYF.ElevenLabsAgents
{
    /// <summary>
    /// Reuses the game's PCM events, transcript bookkeeping and tracked completion protocol.
    /// Receives ElevenLabs PCM while it is generated; no full WAV or AudioClip is required.
    /// </summary>
    internal static class VoiceRouter
    {
        private sealed class Speech
        {
            internal PocketTtsSingleton Owner;
            internal string Text;
            internal string VoicePath;
            internal string Language;
            internal uint CallId;
            internal uint Token;
            internal bool Cancelled;
            internal bool Reported;
        }

        private static readonly Queue<Speech> calls = new Queue<Speech>();
        private static readonly Queue<Speech> narration = new Queue<Speech>();
        private static Plugin plugin;
        private static Speech active;
        private static UnityWebRequest activeHttp;
        private static bool running;
        private static ushort sequence;
        private static uint timestamp;
        private const int SampleRate = 24000;
        private const int FrameSamples = SampleRate / 50;
        private const uint NarrationCallId = 4294967198u;
        private static FieldInfo isSpeaking, activeCall, processingCall, activePrompt, activeTranscript;
        private static FieldInfo transcriptCall, transcriptPrompt, transcriptRevision;
        private static FieldInfo frameEvent, transcriptEvent, completionEvent;

        internal static void Install(Plugin owner)
        {
            plugin = owner;
            Type type = typeof(PocketTtsSingleton);
            isSpeaking = Required(type, "isSpeaking");
            activeCall = Required(type, "activeCallNetId");
            processingCall = Required(type, "processingCallNetId");
            activePrompt = Required(type, "activePrompt");
            activeTranscript = Required(type, "activeTranscript");
            transcriptCall = Required(type, "transcriptCallNetId");
            transcriptPrompt = Required(type, "transcriptPrompt");
            transcriptRevision = Required(type, "transcriptRevision");
            frameEvent = Required(type, "OnCallFrameGenerated");
            transcriptEvent = Required(type, "OnTranscriptGenerated");
            completionEvent = Required(type, "OnTrackedRequestCompleted");
            Patch(owner, "EnqueueSpeak", new[] { typeof(string), typeof(string), typeof(uint), typeof(uint), typeof(string) }, "EnqueuePrefix");
            Patch(owner, "IsBusyForCallInternal", new[] { typeof(uint) }, "BusyPrefix");
            Patch(owner, "CancelCall", new[] { typeof(uint) }, "CancelPrefix");
            Patch(owner, "IsVoiceReady", new[] { typeof(string) }, "VoiceReadyPrefix");
            Patch(owner, "WarmUpVoice", new[] { typeof(string) }, "WarmupPrefix");
        }

        private static FieldInfo Required(Type type, string field)
        {
            var result = AccessTools.Field(type, field);
            if (result == null) throw new MissingFieldException(type.FullName, field);
            return result;
        }

        private static void Patch(Plugin owner, string method, Type[] signature, string prefix)
        {
            if (!PatchCoordinator.TryPatch(owner, typeof(PocketTtsSingleton), method, signature,
                "stream ElevenLabs through the native call audio path",
                prefix: new HarmonyMethod(AccessTools.Method(typeof(VoiceRouter), prefix))))
                throw new InvalidOperationException("ElevenLabs could not install the " + method + " hook in this game build.");
        }

        private static bool EnqueuePrefix(PocketTtsSingleton __instance, string text, string voicePath,
            uint callNetId, uint completionToken, string languageCode)
        {
            if (plugin == null || !plugin.IsVoiceEnabled) return true;
            var item = new Speech { Owner = __instance, Text = text, VoicePath = voicePath ?? "",
                Language = languageCode ?? "", CallId = callNetId, Token = completionToken };
            try
            {
                plugin.VoiceRequests++;
                if (string.IsNullOrWhiteSpace(text)) { Report(item, false); return false; }
                (callNetId == NarrationCallId ? narration : calls).Enqueue(item);
                if (!running)
                {
                    running = true;
                    plugin.StartCoroutine(ProcessQueue());
                }
            }
            catch (Exception ex)
            {
                plugin.Failed("ElevenLabs could not queue speech: " + ex.Message);
                Report(item, false);
            }
            // Once enabled, reject/fail this request locally; never synthesize with another provider.
            return false;
        }

        private static bool BusyPrefix(uint callNetId, ref bool __result)
        {
            if (plugin == null || !plugin.IsVoiceEnabled) return true;
            __result = callNetId != 0 && ((active != null && active.CallId == callNetId && !active.Cancelled)
                || Contains(calls, callNetId) || Contains(narration, callNetId));
            return false;
        }

        private static bool Contains(Queue<Speech> queue, uint id)
        {
            foreach (var item in queue) if (item.CallId == id && !item.Cancelled) return true;
            return false;
        }

        private static bool CancelPrefix(uint callNetId)
        {
            // Always cancel pending bridge speech, even after the mod was toggled off.
            foreach (var item in calls) if (item.CallId == callNetId) { item.Cancelled = true; Report(item, false); }
            foreach (var item in narration) if (item.CallId == callNetId) { item.Cancelled = true; Report(item, false); }
            if (active != null && active.CallId == callNetId)
            {
                active.Cancelled = true;
                if (activeHttp != null) activeHttp.Abort();
            }
            return true;
        }

        private static bool VoiceReadyPrefix(ref bool __result)
        {
            if (plugin == null || !plugin.IsVoiceEnabled) return true;
            __result = true; // The bridge reports missing credentials as an actionable request error.
            return false;
        }

        private static bool WarmupPrefix()
        {
            return plugin == null || !plugin.IsVoiceEnabled;
        }

        internal static void CancelAll()
        {
            foreach (var item in calls) { item.Cancelled = true; Report(item, false); }
            foreach (var item in narration) { item.Cancelled = true; Report(item, false); }
            calls.Clear();
            narration.Clear();
            if (active != null) { active.Cancelled = true; Report(active, false); }
            if (activeHttp != null) activeHttp.Abort();
        }

        internal static void Shutdown()
        {
            CancelAll();
            plugin = null;
        }

        private static IEnumerator ProcessQueue()
        {
            try
            {
                while (calls.Count > 0 || narration.Count > 0)
                {
                    active = calls.Count > 0 ? calls.Dequeue() : narration.Dequeue();
                    if (active.Cancelled || active.Owner == null) { Report(active, false); continue; }
                    var item = active;
                    SetState(item, true);
                    var operation = Stream(item);
                    // Catch iterator errors explicitly: Unity normally just stops a throwing coroutine,
                    // which would otherwise leave tracked requests and isSpeaking stuck forever.
                    while (true)
                    {
                        bool next;
                        object yielded = null;
                        try { next = operation.MoveNext(); if (next) yielded = operation.Current; }
                        catch (Exception ex)
                        {
                            if (plugin != null) plugin.Failed("ElevenLabs playback failed: " + ex.Message);
                            Report(item, false);
                            break;
                        }
                        if (!next) break;
                        yield return yielded;
                    }
                    var disposable = operation as IDisposable;
                    if (disposable != null) disposable.Dispose();
                    SetState(item, false);
                    active = null;
                    if (plugin == null) yield break;
                }
            }
            finally
            {
                if (active != null) { Report(active, false); SetState(active, false); }
                active = null;
                if (activeHttp != null) { activeHttp.Abort(); activeHttp.Dispose(); activeHttp = null; }
                running = false;
            }
        }

        private static IEnumerator Stream(Speech item)
        {
            var body = new JObject { ["input"] = item.Text, ["voice_path"] = item.VoicePath,
                ["language_code"] = item.Language, ["call_net_id"] = item.CallId, ["response_format"] = "pcm" };
            body["caller_context"] = CallerContext.ForSpeech(item.CallId, item.VoicePath, item.Language);
            using (var request = BridgeClient.Post(plugin, "/v1/audio/speech", body))
            {
                request.downloadHandler.Dispose();
                var pcm = new PcmDownloadHandler();
                request.downloadHandler = pcm;
                activeHttp = request;
                try
                {
                var operation = request.SendWebRequest();
                bool headersValidated = false;
                bool transcriptSent = false;
                long framesSent = 0;
                double nextFrameAt = -1;
                var frameBuffer = new float[FrameSamples];
                while (true)
                {
                    if (item.Cancelled || plugin == null || !plugin.IsVoiceEnabled || item.Owner == null)
                    {
                        request.Abort();
                        Report(item, false);
                        yield break;
                    }
                    if (pcm.Overflow) throw new InvalidOperationException("ElevenLabs audio buffer exceeded 16 MiB.");
                    if (!headersValidated && request.responseCode != 0)
                    {
                        if (request.responseCode != 200) throw new InvalidOperationException("ElevenLabs bridge rejected speech (HTTP " + request.responseCode + "). Check bridge credentials/status.");
                        string contentType = request.GetResponseHeader("Content-Type") ?? "";
                        if (!contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("ElevenLabs bridge did not return PCM audio.");
                        string sampleHeader = request.GetResponseHeader("X-Sample-Rate");
                        if (!string.IsNullOrEmpty(sampleHeader) && sampleHeader != SampleRate.ToString())
                            throw new InvalidOperationException("ElevenLabs bridge PCM must be mono 24000 Hz.");
                        headersValidated = true;
                    }
                    if (operation.isDone && request.result != UnityWebRequest.Result.Success)
                        BridgeClient.EnsureSuccess(request, "ElevenLabs");
                    if (headersValidated)
                    {
                        int emittedThisTick = 0;
                        while (pcm.AvailableSamples >= FrameSamples || (operation.isDone && pcm.AvailableSamples > 0))
                        {
                            double now = Time.realtimeSinceStartupAsDouble;
                            if (nextFrameAt < 0) nextFrameAt = now;
                            if (now < nextFrameAt || emittedThisTick >= 8) break;
                            int consumed = pcm.ReadSamples(frameBuffer);
                            if (!transcriptSent) { EmitTranscript(item); transcriptSent = true; }
                            var frames = frameEvent.GetValue(null) as Action<OnAudioFilterReadFrame, string, uint>;
                            if (frames != null) frames(new OnAudioFilterReadFrame(frameBuffer, FrameSamples, SampleRate, 1,
                                sequence++, timestamp), item.VoicePath, item.CallId);
                            timestamp += FrameSamples;
                            framesSent++;
                            emittedThisTick++;
                            nextFrameAt += 0.02;
                            if (consumed < FrameSamples) break;
                        }
                        // Match PocketTTS's native dispatch policy: reset after starvation; allow
                        // eight catch-up frames so low-render-FPS sessions keep the audio flowing.
                        if (!operation.isDone && pcm.AvailableSamples < FrameSamples) nextFrameAt = -1;
                        else if (emittedThisTick == 8 && nextFrameAt <= Time.realtimeSinceStartupAsDouble)
                            nextFrameAt = Time.realtimeSinceStartupAsDouble + 0.02;
                    }
                    if (operation.isDone && pcm.AvailableSamples == 0)
                    {
                        BridgeClient.EnsureSuccess(request, "ElevenLabs");
                        if (!headersValidated || framesSent == 0 || pcm.RemainingBytes != 0)
                            throw new InvalidOperationException("ElevenLabs bridge returned empty or incomplete PCM16 audio.");
                        // Keep native busy/completion state until the last frame's playback window ends.
                        while (Time.realtimeSinceStartupAsDouble < nextFrameAt)
                        {
                            if (item.Cancelled) { Report(item, false); yield break; }
                            yield return null;
                        }
                        plugin.LastOutcome = "ElevenLabs streaming speech completed";
                        plugin.LastError = null;
                        Report(item, true);
                        yield break;
                    }
                    yield return null;
                }
                }
                finally
                {
                    if (activeHttp == request) activeHttp = null;
                }
            }
        }

        private static void SetState(Speech item, bool speaking)
        {
            if (item.Owner == null) return;
            isSpeaking.SetValue(item.Owner, speaking);
            activeCall.SetValue(item.Owner, speaking ? item.CallId : 0u);
            processingCall.SetValue(item.Owner, speaking ? item.CallId : 0u);
            activePrompt.SetValue(item.Owner, speaking ? item.Text.Trim() : null);
            if (speaking)
            {
                transcriptCall.SetValue(item.Owner, item.CallId);
                transcriptPrompt.SetValue(item.Owner, item.Text.Trim());
                activeTranscript.SetValue(item.Owner, "");
                transcriptRevision.SetValue(item.Owner, (int)transcriptRevision.GetValue(item.Owner) + 1);
            }
        }

        private static void EmitTranscript(Speech item)
        {
            // ElevenLabs PCM does not carry word timings. Publish the complete text when audio starts.
            activeTranscript.SetValue(item.Owner, item.Text.Trim());
            transcriptRevision.SetValue(item.Owner, (int)transcriptRevision.GetValue(item.Owner) + 1);
            var updates = transcriptEvent.GetValue(null) as Action<PocketTtsSingleton.PocketTtsTranscriptUpdate>;
            if (updates != null) updates(new PocketTtsSingleton.PocketTtsTranscriptUpdate(item.Text,
                item.Text.Trim(), item.VoicePath, item.CallId, sequence, timestamp));
        }

        private static void Report(Speech item, bool success)
        {
            if (item == null || item.Reported) return;
            item.Reported = true;
            if (item.CallId == 0 || item.Token == 0 || completionEvent == null) return;
            try
            {
                var completed = completionEvent.GetValue(null) as Action<uint, uint, bool>;
                if (completed != null) completed(item.CallId, item.Token, success);
            }
            catch (Exception ex)
            {
                if (plugin != null) plugin.Failed("ElevenLabs tracked completion callback failed: " + ex.Message);
            }
        }
    }
}
