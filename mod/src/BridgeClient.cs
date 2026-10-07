using System;
using System.Collections;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ScamWYF.ElevenLabsAgents
{
    internal static class BridgeClient
    {
        internal static UnityWebRequest Post(Plugin plugin, string path, JObject body)
        {
            var request = new UnityWebRequest(plugin.Endpoint(path), "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = plugin.RequestTimeout;
            return request;
        }

        internal static IEnumerator Chat(Plugin plugin, JObject body, CancellationToken cancellation,
            UniTaskCompletionSource<JObject> completion)
        {
            UnityWebRequest request = null;
            UnityWebRequestAsyncOperation operation = null;
            try
            {
                if (cancellation.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellation);
                    yield break;
                }
                if (body == null) throw new ArgumentException("Game chat request is empty.");
                var outbound = (JObject)body.DeepClone();
                // The game's await consumes a complete choices[0].message.content JSON envelope.
                outbound["stream"] = false;
                request = Post(plugin, "/v1/chat/completions", outbound);
                operation = request.SendWebRequest();
            }
            catch (Exception ex)
            {
                plugin.Failed("ElevenLabs Agents bridge request could not start: " + ex.Message);
                completion.TrySetException(ex);
                if (request != null) request.Dispose();
            }
            if (operation == null) yield break;
            using (request)
            {
                while (!operation.isDone)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        request.Abort();
                        completion.TrySetCanceled(cancellation);
                        yield break;
                    }
                    yield return null;
                }
                try
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellation);
                        yield break;
                    }
                    EnsureSuccess(request, "ElevenLabs Agents");
                    var reply = JObject.Parse(request.downloadHandler.text);
                    var content = reply.SelectToken("choices[0].message.content");
                    if (content == null || content.Type != JTokenType.String)
                        throw new InvalidOperationException("ElevenLabs Agents bridge response has no choices[0].message.content string.");
                    plugin.LastOutcome = "ElevenLabs Agent reply received";
                    plugin.LastError = null;
                    completion.TrySetResult(reply);
                }
                catch (Exception ex)
                {
                    plugin.Failed(ex.Message);
                    completion.TrySetException(ex);
                }
            }
        }

        internal static void EnsureSuccess(UnityWebRequest request, string provider)
        {
            if (request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300) return;
            // Response bodies and provider credentials never enter the game log.
            throw new InvalidOperationException(provider + " bridge request failed (HTTP " + request.responseCode
                + ", " + request.error + "). See the local bridge status/log for details.");
        }

        internal static IEnumerator PollHealth(Plugin plugin)
        {
            while (plugin != null)
            {
                if (plugin.BridgeEnabled.Value)
                {
                    UnityWebRequest request = null;
                    UnityWebRequestAsyncOperation operation = null;
                    try
                    {
                        request = UnityWebRequest.Get(plugin.Endpoint("/health"));
                        request.timeout = 3;
                        operation = request.SendWebRequest();
                    }
                    catch (Exception) { plugin.BridgeReachable = false; }
                    if (operation != null)
                    {
                        using (request)
                        {
                            yield return operation;
                            plugin.BridgeReachable = request.result == UnityWebRequest.Result.Success && request.responseCode == 200;
                            plugin.AgentConfigured = false;
                            plugin.VoiceConfigured = false;
                            if (plugin.BridgeReachable)
                            {
                                try
                                {
                                    var health = JObject.Parse(request.downloadHandler.text);
                                    plugin.AgentConfigured = health["configured"] != null && (bool)health["configured"];
                                    plugin.VoiceConfigured = health["voiceConfigured"] != null && (bool)health["voiceConfigured"];
                                }
                                catch (Exception) { /* A reachable endpoint may need updated setup. */ }
                            }
                        }
                    }
                    else if (request != null) request.Dispose();
                }
                else { plugin.BridgeReachable = false; plugin.AgentConfigured = false; plugin.VoiceConfigured = false; }
                yield return new WaitForSecondsRealtime(5f);
            }
        }
    }
}
