import http from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import { loadConfig, normalizeConfig, saveConfig, publicConfig, bridgeRoot } from './config.js';
import { createAgentsProvider } from './agents.js';
import { validateRequest, normalizeContent, completionResponse } from './completion.js';
import { SpeechCache, sampleRate, wav } from './speech.js';
import { provisionAgent, readAgentConfiguration } from './provision.js';
import { CallerProfiles, languageCode } from './caller-profiles.js';
import { getModelCapabilities } from './voice-catalog.js';
import { ensureVoiceOverrides } from './voice-settings.js';

const json = (res, status, data) => { res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(data)); };
async function bodyJson(req) {
  let size = 0; const chunks = [];
  for await (const chunk of req) { size += chunk.length; if (size > 3_000_000) throw new Error('Anfrage zu groß.'); chunks.push(chunk); }
  try { return JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { throw new Error('Anfrage ist kein gültiges JSON.'); }
}
function abortable(promise, signal) {
  signal.throwIfAborted(); let stop;
  const aborted = new Promise((_, reject) => { stop = () => reject(signal.reason); signal.addEventListener('abort', stop, { once: true }); });
  return Promise.race([promise, aborted]).finally(() => signal.removeEventListener('abort', stop));
}
export function createBridge(initialConfig, deps = {}) {
  let cfg = normalizeConfig(initialConfig);
  const provider = deps.provider ?? createAgentsProvider(), persist = deps.saveConfig ?? saveConfig;
  const provision = deps.provisionAgent ?? provisionAgent;
  const speech = deps.speech ?? new SpeechCache({ createSpeech: provider.speak });
  const profiles = deps.profiles ?? new CallerProfiles();
  const active = new Set(); let lastMetrics = null;
  const server = http.createServer(async (req, res) => {
    const requestConfig = { ...cfg }, controller = new AbortController(), signal = controller.signal;
    active.add(controller);
    const disconnected = () => { if (!res.writableEnded) controller.abort(new Error('Verbindung beendet.')); };
    res.on('close', disconnected); req.on('aborted', disconnected);
    const timer = setTimeout(() => controller.abort(new Error('Anfrage-Zeitlimit überschritten.')), requestConfig.REQUEST_TIMEOUT_SECONDS * 1000); timer.unref();
    let audio;
    try {
      const host = req.headers.host ?? '';
      if (!/^(127\.0\.0\.1|localhost)(:\d+)?$/.test(host)) return json(res, 403, { error: { message: 'Nur localhost ist erlaubt.' } });
      if (req.headers.origin && req.headers.origin !== `http://${host}`) return json(res, 403, { error: { message: 'Fremder Origin ist nicht erlaubt.' } });
      const url = new URL(req.url, `http://${host}`);
      if (req.method === 'GET' && url.pathname === '/') {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'", 'X-Content-Type-Options': 'nosniff' });
        return res.end(await readFile(path.join(bridgeRoot, 'public', 'index.html')));
      }
      if (req.method === 'GET' && ['/app.js', '/style.css'].includes(url.pathname)) {
        res.writeHead(200, { 'Content-Type': url.pathname.endsWith('.js') ? 'text/javascript; charset=utf-8' : 'text/css; charset=utf-8' });
        return res.end(await readFile(path.join(bridgeRoot, 'public', url.pathname.slice(1))));
      }
      if (req.method === 'GET' && ['/health', '/api/status'].includes(url.pathname)) return json(res, 200, { ok: true, ...publicConfig(cfg), lastMetrics });
      if (req.method === 'GET' && url.pathname === '/agent-template.json') return json(res, 200, JSON.parse(await readFile(path.join(bridgeRoot, 'agent-template.json'), 'utf8')));
      if (req.method === 'GET' && url.pathname === '/api/voices') return json(res, 200, { voices: await abortable(profiles.voices(requestConfig, signal), signal) });
      if (req.method === 'GET' && url.pathname === '/api/callers') return json(res, 200, { callers: await profiles.list() });
      if (req.method === 'GET' && url.pathname === '/api/voice-models') return json(res, 200, { models: await abortable(getModelCapabilities(requestConfig, { signal }), signal) });
      if (req.method !== 'POST') return json(res, 404, { error: { message: 'Endpunkt nicht gefunden.' } });
      if (!String(req.headers['content-type'] ?? '').startsWith('application/json')) return json(res, 415, { error: { message: 'Content-Type application/json erforderlich.' } });
      const body = await bodyJson(req);
      if (url.pathname === '/api/config') {
        const update = { ...cfg, ...body, PORT: cfg.PORT };
        if (!body.ELEVENLABS_API_KEY && !body.CLEAR_ELEVENLABS_API_KEY) update.ELEVENLABS_API_KEY = cfg.ELEVENLABS_API_KEY;
        const updated = normalizeConfig(update); await persist(updated); cfg = updated;
        return json(res, 200, publicConfig(cfg));
      }
      if (url.pathname === '/api/create-agent') {
        const created = await abortable(provision(requestConfig, { signal }), signal);
        const updated = normalizeConfig({ ...cfg, ELEVENLABS_AGENT_ID: created.agentId, ELEVENLABS_GAME_TOOL_ID: created.toolId });
        try { await persist(updated); }
        catch { throw new Error(`Agent ${created.agentId} und Tool ${created.toolId} wurden angelegt. Lokales Speichern ist fehlgeschlagen; IDs aus dem Dashboard übernehmen, bevor ein weiterer Agent angelegt wird.`); }
        cfg = updated; return json(res, 200, publicConfig(cfg));
      }
      if (url.pathname === '/api/check-agent') {
        const checked = await abortable(readAgentConfiguration(requestConfig, { signal }), signal);
        if (checked.ready && checked.gameToolId && !cfg.ELEVENLABS_GAME_TOOL_ID) {
          const updated = normalizeConfig({ ...cfg, ELEVENLABS_GAME_TOOL_ID: checked.gameToolId }); await persist(updated); cfg = updated;
        }
        return json(res, 200, checked);
      }
      if (url.pathname === '/api/enable-character-voices') return json(res, 200, await abortable(ensureVoiceOverrides(requestConfig, { signal }), signal));
      if (url.pathname === '/api/callers') return json(res, 200, { caller: await abortable(profiles.update(body, requestConfig, signal), signal) });
      if (url.pathname === '/api/preview-voice') {
        const caller = (await profiles.list()).find(entry => entry.id === body.caller_id);
        const voice = (await abortable(profiles.voices(requestConfig, signal), signal)).find(entry => entry.id === body.voice_id);
        const language = languageCode(body.language_code);
        if (!caller || !voice || !language) return json(res, 400, { error: { message: 'Anrufer, Stimme oder Sprache ist ungültig.' } });
        const samples = { en: 'Hello, who am I speaking with?', de: 'Guten Tag, mit wem spreche ich?', fr: 'Bonjour, à qui ai-je affaire ?', es: 'Hola, ¿con quién estoy hablando?', it: 'Buongiorno, con chi sto parlando?', zh: '你好，请问你是哪位？', ja: 'こんにちは、どちら様ですか？', ko: '안녕하세요, 누구세요?', hi: 'नमस्ते, आप कौन बोल रहे हैं?', ru: 'Здравствуйте, с кем я разговариваю?', pt: 'Olá, com quem estou falando?' };
        if (!samples[language]) return json(res, 400, { error: { message: 'Für diese Sprache gibt es noch keinen Vorschautext.' } });
        const profile = { callerId: caller.id, callerName: caller.name, language, playerLanguage: requestConfig.ELEVENLABS_LANGUAGE, voiceId: voice.id, model: requestConfig.ELEVENLABS_TTS_MODEL, callKey: `preview-${caller.id}` };
        audio = await abortable(provider.speak(samples[language], requestConfig, signal, profile), signal);
        const result = wav(await abortable(audio.done, signal));
        res.writeHead(200, { 'Content-Type': 'audio/wav', 'Content-Length': result.length, 'Cache-Control': 'no-store' }); return res.end(result);
      }
      if (active.size > 8) return json(res, 429, { error: { message: 'Zu viele gleichzeitige Anfragen.' } });
      if (url.pathname === '/v1/audio/transcriptions') {
        const result = await abortable(provider.transcribe(body, requestConfig, signal), signal);
        return json(res, 200, result);
      }
      if (url.pathname === '/v1/audio/speech') {
        const text = body.input ?? body.text;
        if (typeof text !== 'string' || !text.trim() || text.length > 6000) return json(res, 400, { error: { message: 'Sprachtext fehlt oder überschreitet 6000 Zeichen.' } });
        if (body.response_format && !['pcm', 'wav'].includes(body.response_format)) return json(res, 400, { error: { message: 'Audioformat muss pcm oder wav sein.' } });
        const profile = await abortable(profiles.resolve(body, requestConfig, signal), signal);
        if (body.response_format !== 'pcm') {
          const result = await abortable(speech.audio(text, requestConfig, signal, profile), signal);
          res.writeHead(200, { 'Content-Type': 'audio/wav', 'Content-Length': result.length, 'Cache-Control': 'no-store' }); return res.end(result);
        }
        audio = await abortable(speech.take(text, requestConfig, signal, profile), signal); let started = false;
        const stop = audio.subscribe(chunk => {
          if (signal.aborted || res.destroyed) return;
          if (!started) { started = true; res.writeHead(200, { 'Content-Type': 'audio/pcm', 'X-Sample-Rate': String(sampleRate), 'X-Channels': '1', 'X-Audio-Format': 's16le-mono', 'Cache-Control': 'no-store' }); }
          res.write(chunk);
        });
        const cancel = () => audio.fail(new Error('Agent-Sprachausgabe abgebrochen.')); signal.addEventListener('abort', cancel, { once: true });
        try { await abortable(audio.done, signal); if (!started) throw new Error('Keine Audiodaten.'); res.end(); }
        finally { stop(); signal.removeEventListener('abort', cancel); }
        return;
      }
      const test = url.pathname === '/api/test';
      if (url.pathname !== '/v1/chat/completions' && !test) return json(res, 404, { error: { message: 'Endpunkt nicht gefunden.' } });
      const chat = test ? {
        messages: [{ role: 'system', content: 'Du bist ein freundlicher fiktiver Anrufer. Antworte auf Deutsch.' }, { role: 'user', content: 'Sag einen kurzen Begrüßungssatz.' }], max_tokens: 128,
        response_format: { type: 'json_schema', json_schema: { name: 'caller_turn', schema: { type: 'object', properties: { dialogue: { type: 'string' }, trust_percent: { type: 'integer', minimum: 0, maximum: 100 }, emotion: { type: 'string', enum: ['TRUSTING', 'SUSPICIOUS', 'ANGRY', 'NEUTRAL'] } }, required: ['dialogue', 'trust_percent', 'emotion'], additionalProperties: false } } },
      } : body;
      validateRequest(chat);
      const profile = await abortable(profiles.resolve(chat, requestConfig, signal), signal);
      chat._speech_profile = profile;
      const result = await abortable(provider.complete(chat, requestConfig, signal), signal);
      audio = result.audio;
      const content = normalizeContent(result.content, chat);
      if (audio) speech.put(JSON.parse(content).dialogue, requestConfig, audio, profile);
      lastMetrics = result.metrics;
      const id = `chatcmpl-${randomUUID()}`;
      if (chat.stream) {
        res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
        const chunk = { id, object: 'chat.completion.chunk', model: 'elevenlabs-agent', choices: [{ index: 0, delta: { content }, finish_reason: null }] };
        res.write(`data: ${JSON.stringify(chunk)}\n\n`); chunk.choices[0] = { index: 0, delta: {}, finish_reason: 'stop' }; res.end(`data: ${JSON.stringify(chunk)}\n\ndata: [DONE]\n\n`);
      } else json(res, 200, test ? { content, metrics: result.metrics, voiceReady: publicConfig(requestConfig).voiceConfigured } : completionResponse(content, 'elevenlabs-agent', id));
    } catch (error) {
      if (audio && !res.writableEnded) audio.fail(error);
      const message = String((signal.aborted ? signal.reason : error)?.message ?? 'Agent-Anfrage fehlgeschlagen.').split(requestConfig.ELEVENLABS_API_KEY || '\0').join('[redacted]').slice(0, 700);
      if (!res.headersSent) json(res, signal.aborted ? 504 : 502, { error: { message } }); else res.destroy();
    } finally { clearTimeout(timer); active.delete(controller); res.removeListener('close', disconnected); }
  });
  server.requestTimeout = 310_000; server.headersTimeout = 10_000;
  server.on('close', () => { for (const controller of active) controller.abort(new Error('Backend beendet.')); speech.close(); });
  return server;
}
async function main() {
  const cfg = await loadConfig(), server = createBridge(cfg);
  server.listen(cfg.PORT, '127.0.0.1', () => console.log(`ElevenLabs Agents: http://127.0.0.1:${cfg.PORT}\nSchlüssel und Agent auf dieser lokalen Seite einrichten.`));
  server.on('error', error => { console.error(error.code === 'EADDRINUSE' ? `Port ${cfg.PORT} ist bereits belegt.` : 'Backend konnte nicht starten.'); process.exitCode = 1; });
  const shutdown = () => { server.close(); server.closeAllConnections(); };
  process.once('SIGINT', shutdown); process.once('SIGTERM', shutdown);
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main().catch(error => { console.error(error.message); process.exitCode = 1; });
