using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using ScamWYF.Modding.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace ScamWYF.ElevenLabsAgents
{
    /// <summary>Keep the game's VAD/resampling and replace final Whisper inference with Agents ASR.</summary>
    internal static class RecognitionRouter
    {
        private sealed class Inference
        {
            internal object Pipeline;
            internal object Request;
            internal int Generation;
            internal TaskCompletionSource<bool> Completion;
            internal UnityWebRequest Http;
            internal bool Cancelled;
        }

        private static Plugin plugin;
        private static MethodInfo runInference;
        private static FieldInfo requestSamples, requestFinal, requestGeneration, requestSpeech;
        private static FieldInfo generation, inProgress, activeInference, activeRecognizer;
        private static FieldInfo pendingFinals, transcriptRange, lastPartial, nextPartialAt, transcriptEvent;
        private static readonly List<Inference> pending = new List<Inference>();

        internal static void Install(Plugin owner)
        {
            plugin = owner;
            var type = AccessTools.TypeByName("WhisperVcPipeline");
            if (type == null) throw new TypeLoadException("Game WhisperVcPipeline was not found.");
            runInference = AccessTools.Method(type, "RunInferenceAsync");
            if (runInference == null) throw new MissingMethodException(type.FullName, "RunInferenceAsync");
            var requestType = runInference.GetParameters()[0].ParameterType;
            requestSamples = Required(requestType, "Samples");
            requestFinal = Required(requestType, "IsFinal");
            requestGeneration = Required(requestType, "Generation");
            requestSpeech = Required(requestType, "Speech");
            generation = Required(type, "recognitionGeneration");
            inProgress = Required(type, "inferenceInProgress");
            activeInference = Required(type, "activeInference");
            activeRecognizer = Required(type, "activeRecognizer");
            pendingFinals = Required(type, "queuedFinalInferences");
            transcriptRange = Required(type, "<TranscriptRange>k__BackingField");
            lastPartial = Required(type, "lastPartialTranscript");
            nextPartialAt = Required(type, "nextPartialInferenceAt");
            transcriptEvent = Required(type, "OnTranscriptUpdated");
            if (!PatchCoordinator.TryPatch(owner, runInference, "recognize completed microphone utterances with ElevenLabs Agents",
                prefix: new HarmonyMethod(AccessTools.Method(typeof(RecognitionRouter), "InferencePrefix"))))
                throw new InvalidOperationException("Could not install ElevenLabs Agents speech recognition hook.");
        }

        private static FieldInfo Required(Type type, string name)
        {
            var result = AccessTools.Field(type, name);
            if (result == null) throw new MissingFieldException(type.FullName, name);
            return result;
        }

        private static bool InferencePrefix(object __instance, object request, ref Task __result)
        {
            if (plugin == null || !plugin.BridgeEnabled.Value || !plugin.RecognitionEnabled.Value) return true;
            var completion = new TaskCompletionSource<bool>();
            __result = completion.Task;
            try
            {
                if (request == null || !(bool)requestFinal.GetValue(request))
                {
                    // Each partial contains the complete utterance-so-far. Sending it to an Agent
                    // would generate extra turns; final game VAD segments are submitted exactly once.
                    nextPartialAt.SetValue(__instance, Time.realtimeSinceStartupAsDouble + 1d);
                    completion.TrySetResult(false);
                    return false;
                }
                var item = new Inference { Pipeline = __instance, Request = request,
                    Generation = (int)requestGeneration.GetValue(request), Completion = completion };
                inProgress.SetValue(__instance, true);
                activeInference.SetValue(__instance, request);
                pending.Add(item);
                plugin.RecognitionRequests++;
                plugin.StartCoroutine(Run(item));
            }
            catch (Exception ex)
            {
                plugin.Failed("ElevenLabs Agents recognition could not start: " + ex.Message);
                inProgress.SetValue(__instance, false);
                activeInference.SetValue(__instance, null);
                completion.TrySetResult(false);
            }
            return false;
        }

        private static IEnumerator Run(Inference item)
        {
            UnityWebRequestAsyncOperation operation = null;
            try
            {
                if (!IsCurrent(item)) yield break;
                var samples = requestSamples.GetValue(item.Request) as float[];
                if (samples == null || samples.Length == 0) yield break;
                var bytes = new byte[checked(samples.Length * 2)];
                for (int i = 0; i < samples.Length; i++)
                {
                    float sample = samples[i];
                    if (float.IsNaN(sample) || float.IsInfinity(sample)) sample = 0;
                    int value = (int)(Math.Max(-1f, Math.Min(1f, sample)) * 32767f);
                    bytes[i * 2] = (byte)value;
                    bytes[i * 2 + 1] = (byte)(value >> 8);
                }
                object speech = requestSpeech.GetValue(item.Request);
                uint callId = 0;
                if (speech != null)
                {
                    var callField = AccessTools.Field(speech.GetType(), "callId");
                    if (callField != null) callId = (uint)callField.GetValue(speech);
                }
                var body = new JObject { ["audio_base64"] = Convert.ToBase64String(bytes),
                    ["sample_rate"] = 16000, ["is_final"] = true, ["call_id"] = callId };
                body["player_language_code"] = CallerContext.PlayerLanguage();
                body["language_code"] = body["player_language_code"].DeepClone();
                try
                {
                    item.Http = BridgeClient.Post(plugin, "/v1/audio/transcriptions", body);
                    operation = item.Http.SendWebRequest();
                }
                catch (Exception ex) { plugin.Failed("ElevenLabs Agents recognition request failed: " + ex.Message); }
                if (operation == null) yield break;
                while (!operation.isDone)
                {
                    if (!IsCurrent(item)) { item.Http.Abort(); yield break; }
                    yield return null;
                }
                if (!IsCurrent(item)) yield break;
                try
                {
                    BridgeClient.EnsureSuccess(item.Http, "ElevenLabs Agents ASR");
                    var reply = JObject.Parse(item.Http.downloadHandler.text);
                    string text = (string)reply["text"];
                    string language = (string)reply["language_code"] ?? "";
                    // Agents supplies no ASR confidence. 1 is only the native event compatibility
                    // value so the game's threshold filter does not discard valid Agent transcripts.
                    float confidence = reply["confidence"] == null ? 1f : (float)reply["confidence"];
                    if (text == null) throw new InvalidOperationException("ElevenLabs Agents recognition response has no text string.");
                    lastPartial.SetValue(item.Pipeline, "");
                    nextPartialAt.SetValue(item.Pipeline, Time.realtimeSinceStartupAsDouble + 1d);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        transcriptRange.SetValue(item.Pipeline, requestSpeech.GetValue(item.Request));
                        var transcript = transcriptEvent.GetValue(item.Pipeline) as Action<string, float, bool, string>;
                        if (transcript != null) transcript(text.Trim(), Math.Max(0f, Math.Min(1f, confidence)), false, language);
                    }
                    plugin.LastOutcome = "ElevenLabs Agent microphone transcript received";
                    plugin.LastError = null;
                }
                catch (Exception ex) { plugin.Failed(ex.Message); }
            }
            finally
            {
                if (item.Http != null) { item.Http.Dispose(); item.Http = null; }
                pending.Remove(item);
                bool stillOwned = ReferenceEquals(activeInference.GetValue(item.Pipeline), item.Request);
                if (stillOwned)
                {
                    inProgress.SetValue(item.Pipeline, false);
                    activeInference.SetValue(item.Pipeline, null);
                    activeRecognizer.SetValue(item.Pipeline, null);
                }
                item.Completion.TrySetResult(true);
                // Match the game's final-inference queue. A new final may arrive during the request.
                if (plugin != null && stillOwned)
                {
                    var queue = pendingFinals.GetValue(item.Pipeline);
                    var count = queue.GetType().GetProperty("Count");
                    if ((int)count.GetValue(queue, null) > 0)
                    {
                        var next = queue.GetType().GetMethod("Dequeue").Invoke(queue, null);
                        runInference.Invoke(item.Pipeline, new[] { next });
                    }
                }
            }
        }

        private static bool IsCurrent(Inference item)
        {
            // A suspended final is accepted just as in the game. ResetRecognition changes Generation.
            return !item.Cancelled && plugin != null && plugin.BridgeEnabled.Value && plugin.RecognitionEnabled.Value
                && item.Pipeline != null && item.Generation == (int)generation.GetValue(item.Pipeline);
        }

        internal static void Shutdown()
        {
            plugin = null;
            foreach (var item in pending)
            {
                item.Cancelled = true;
                if (item.Http != null) item.Http.Abort();
                if (ReferenceEquals(activeInference.GetValue(item.Pipeline), item.Request))
                {
                    inProgress.SetValue(item.Pipeline, false);
                    activeInference.SetValue(item.Pipeline, null);
                }
                item.Completion.TrySetCanceled();
            }
        }
    }
}
