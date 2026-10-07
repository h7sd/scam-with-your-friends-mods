import WebSocket from 'ws';
import { AudioBuffer } from './speech.js';
import { buildPrompt } from './completion.js';
import { callerContext, genericProfile } from './caller-profiles.js';
const moods = ['TRUSTING', 'SUSPICIOUS', 'ANGRY', 'NEUTRAL'];
export function validState(value) { return value && Number.isInteger(value.trust_percent) && value.trust_percent >= 0 && value.trust_percent <= 100 && moods.includes(value.emotion); }
export async function signedUrl(cfg, signal, fetchImpl = fetch) {
  if (!cfg.ELEVENLABS_API_KEY || !cfg.ELEVENLABS_AGENT_ID) throw new Error('ElevenLabs-Schlüssel oder Agent-ID fehlt.');
  const response = await fetchImpl(`https://api.elevenlabs.io/v1/convai/conversation/get-signed-url?agent_id=${encodeURIComponent(cfg.ELEVENLABS_AGENT_ID)}`, { headers: { 'xi-api-key': cfg.ELEVENLABS_API_KEY }, signal });
  if (!response.ok) throw new Error(`ElevenLabs Agent-Zugriff abgelehnt (HTTP ${response.status}). Schlüssel, Agent-ID und Agents-Berechtigung prüfen.`);
  const url = new URL((await response.json()).signed_url);
  if (url.protocol !== 'wss:' || url.hostname !== 'api.elevenlabs.io') throw new Error('Ungültige ElevenLabs-Verbindungsadresse.');
  return url.href;
}
export class AgentSession {
  constructor(cfg, signal, { url, WebSocketClass = WebSocket, prompt, textOnly = false, gameTool = false, profile, language = cfg.ELEVENLABS_LANGUAGE, onEvent = () => {} } = {}) {
    if (gameTool && !cfg.ELEVENLABS_GAME_TOOL_ID) throw new Error('Spielstatus-Tool fehlt. Agent auf der lokalen Einrichtungsseite anlegen.');
    this.cfg = cfg; this.signal = signal; this.audio = new AudioBuffer(); this.closed = false; this.text = ''; this.state = null;
    this.ready = new Promise((resolve, reject) => { this.readyResolve = resolve; this.readyReject = reject; }); this.ready.catch(() => {});
    this.result = new Promise((resolve, reject) => { this.resultResolve = resolve; this.resultReject = reject; }); this.result.catch(() => {});
    this.transcript = new Promise((resolve, reject) => { this.transcriptResolve = resolve; this.transcriptReject = reject; }); this.transcript.catch(() => {});
    this.gameTool = gameTool; this.textOnly = textOnly;
    this.socket = new WebSocketClass(url, { maxPayload: 2_000_000 });
    this.abort = () => this.fail(signal?.reason ?? new Error('Agent-Anfrage abgebrochen.'));
    signal?.addEventListener('abort', this.abort, { once: true });
    this.timer = setTimeout(() => this.fail(new Error('ElevenLabs Agent-Zeitlimit überschritten.')), cfg.REQUEST_TIMEOUT_SECONDS * 1000); this.timer.unref?.();
    this.socket.on('open', () => this.send({ type: 'conversation_initiation_client_data', conversation_config_override: {
      agent: { first_message: '', language, prompt: { prompt, tool_ids: gameTool ? [cfg.ELEVENLABS_GAME_TOOL_ID] : [] } },
      conversation: { text_only: textOnly },
      ...(!textOnly && profile ? { tts: { ...(profile.voiceId ? { voice_id: profile.voiceId } : {}), model_id: profile.model, stability: 0.5, similarity_boost: 0.75 } } : {}),
    } }));
    this.socket.on('message', raw => {
      if (this.closed) return;
      try {
        const event = JSON.parse(raw.toString()); onEvent(event);
        if (event.type === 'ping') { this.send({ type: 'pong', event_id: event.ping_event.event_id }); return; }
        if (event.type === 'conversation_initiation_metadata') {
          const meta = event.conversation_initiation_metadata_event;
          if (!textOnly && meta.agent_output_audio_format !== 'pcm_24000') throw new Error('Agent-Audio muss pcm_24000 sein. Agent über die lokale Einrichtungsseite neu anlegen.');
          this.inputFormat = meta.user_input_audio_format; this.readyResolve(meta);
        } else if (event.type === 'user_transcript') this.transcriptResolve(event.user_transcription_event.user_transcript);
        else if (event.type === 'agent_response') {
          const text = event.agent_response_event.agent_response;
          if (typeof text === 'string') { this.text = text; this.maybeResolve(); }
        } else if (event.type === 'agent_response_correction') throw new Error('Agent-Antwort wurde korrigiert; der Spielzug wird verworfen.');
        else if (event.type === 'audio') this.audio.push(Buffer.from(event.audio_event.audio_base_64, 'base64'));
        else if (event.type === 'client_tool_call') {
          const tool = event.client_tool_call, accepted = gameTool && tool.tool_name === 'submit_game_turn' && validState(tool.parameters);
          if (accepted) this.state = { trust_percent: tool.parameters.trust_percent, emotion: tool.parameters.emotion };
          this.send({ type: 'client_tool_result', tool_call_id: tool.tool_call_id, is_error: !accepted, result: accepted ? 'Game state accepted. Now speak your short caller reply, with no JSON or metadata.' : 'Rejected: only valid submit_game_turn state is allowed.' });
          if (!accepted) throw new Error('Agent hat ein unbekanntes oder ungültiges Spielstatus-Tool aufgerufen.');
          this.maybeResolve();
        } else if (event.type === 'agent_response_complete') {
          this.maybeResolve();
          if (!this.text || (gameTool && !this.state)) throw new Error('Agent hat keine vollständige Antwort mit Spielstatus geliefert.');
          if (textOnly) this.close(); else this.audio.finish();
        } else if (['client_error', 'guardrail_triggered', 'interruption'].includes(event.type)) throw new Error(`ElevenLabs Agents: ${event.type}. Overrides, Tool und Agent-Konfiguration prüfen.`);
        else if (event.type === 'queue_status' && event.queue_status_event?.status === 'timed_out') throw new Error('ElevenLabs Agents: Parallelitätslimit überschritten.');
      } catch (error) { this.fail(error); }
    });
    this.socket.on('error', () => this.fail(new Error('ElevenLabs Agents WebSocket-Verbindung fehlgeschlagen.')));
    this.socket.on('close', () => { if (!this.closed) this.fail(new Error('ElevenLabs Agent-Verbindung vorzeitig beendet.')); });
    this.audio.onFinish = () => { if (this.audio.error) this.fail(this.audio.error); else this.close(); };
    if (signal?.aborted) this.abort();
  }
  send(data) { if (!this.closed && this.socket.readyState === 1) this.socket.send(JSON.stringify(data)); }
  startSilentInput() {
    if (this.silentInputTimer || this.closed) return;
    if (this.inputFormat !== 'pcm_16000') throw new Error('Agent-Eingabeformat muss pcm_16000 sein.');
    // Agents' voice processing advances on incoming microphone frames. A text-triggered
    // caller has no live mic, so keep delivering 100ms of silence until audio completes.
    // The same tail lets short recorded utterances reach the ASR/VAD commit threshold.
    const silence = Buffer.alloc(16000 * 2 / 10).toString('base64');
    const tick = () => this.send({ user_audio_chunk: silence });
    tick(); this.silentInputTimer = setInterval(tick, 100); this.silentInputTimer.unref?.();
  }
  maybeResolve() { if (this.text && (!this.gameTool || this.state)) this.resultResolve(this.gameTool ? JSON.stringify({ dialogue: this.text, ...this.state }) : this.text); }
  detachSignal() { this.signal?.removeEventListener('abort', this.abort); }
  close() { if (this.closed) return; this.closed = true; clearTimeout(this.timer); clearInterval(this.silentInputTimer); this.detachSignal(); if (this.socket.readyState === 0) this.socket.terminate(); else if (this.socket.readyState === 1) this.socket.close(); }
  fail(error) { this.readyReject(error); this.resultReject(error); this.transcriptReject(error); this.audio.fail(error); this.close(); }
}
export function createAgentsProvider({ fetchImpl = fetch, WebSocketClass = WebSocket, getUrl = signedUrl } = {}) {
  const start = async (cfg, signal, options) => new AgentSession(cfg, signal, { ...options, url: await getUrl(cfg, signal, fetchImpl), WebSocketClass });
  return {
    async complete(body, cfg, signal) {
      const schema = body.response_format?.json_schema?.schema ?? body.response_format?.schema, caller = schema?.properties?.dialogue?.type === 'string';
      const profile = body._speech_profile ?? genericProfile(body, cfg);
      const prompt = caller ? [
        'You are a fictional caller in Scam With Your Friends. Follow the personality and game situation from the serialized chat conversation. Respond in the requested caller language.',
        'For EACH turn, FIRST call submit_game_turn with trust_percent (integer 0–100) and emotion (TRUSTING, SUSPICIOUS, ANGRY, NEUTRAL). Then speak ONLY your short caller dialogue in normal words. Replace any JSON formatting instructions from the game conversation with this tool-plus-speech protocol. Never speak JSON, trust values, tool names or narration.',
        `Game conversation and context: ${JSON.stringify(body.messages)}`,
        `The spoken language for THIS caller is ${profile.language}. Speak only that language. This replaces any older requested response language in the serialized game messages. The player's microphone language is ${profile.playerLanguage}; it does not change the caller's language. Keep the caller's accent, age and personality natural and concise.`,
      ].join('\n\n') : buildPrompt(body);
      const startTime = performance.now(), metrics = { firstTextMs: null, firstAudioMs: null, completionMs: null, speechPrefetched: caller };
      const session = await start(cfg, signal, { prompt, textOnly: !caller, gameTool: caller, profile: caller ? profile : undefined, language: caller ? profile.language : profile.playerLanguage, onEvent: event => {
        if (event.type === 'agent_response' && metrics.firstTextMs === null) metrics.firstTextMs = Math.round(performance.now() - startTime);
        if (event.type === 'audio' && metrics.firstAudioMs === null) metrics.firstAudioMs = Math.round(performance.now() - startTime);
      } });
      try {
        await session.ready; signal.throwIfAborted();
        if (caller) session.startSilentInput();
        session.send({ type: 'user_message', text: caller ? 'Continue the caller conversation from the last supplied message. Submit game state, then speak your reply.' : 'Return the exact JSON result for the supplied game request now.' });
        const content = await session.result; signal.throwIfAborted(); metrics.completionMs = Math.round(performance.now() - startTime);
        if (caller) session.detachSignal(); else session.close();
        Object.assign(metrics, { callerLanguage: caller ? profile.language : null, voiceId: caller ? profile.voiceId : null, speechModel: caller ? profile.model : null });
        return { content, audio: caller ? session.audio : null, metrics };
      } catch (error) { session.fail(error); throw error; }
    },
    async speak(text, cfg, signal, profile = genericProfile({}, cfg)) {
      const session = await start(cfg, signal, { prompt: `You provide audio inside a fictional video game. Repeat EXACTLY the text in the user message in language ${profile.language}. Do not translate, add commentary or change any words. No tools.`, textOnly: false, profile, language: profile.language });
      try { await session.ready; signal.throwIfAborted(); session.startSilentInput(); session.send({ type: 'user_message', text }); return session.audio; }
      catch (error) { session.fail(error); throw error; }
    },
    async transcribe(body, cfg, signal) {
      if (body.sample_rate !== 16000 || typeof body.audio_base64 !== 'string') throw new Error('Mikrofon-Audio muss s16le mono 16000Hz sein.');
      const pcm = Buffer.from(body.audio_base64, 'base64');
      if (!pcm.length || pcm.length % 2 || pcm.length > 16000 * 2 * 60) throw new Error('Ungültiges Mikrofon-Audio.');
      const playerLanguage = callerContext(body).playerLanguage || body.player_language_code || cfg.ELEVENLABS_LANGUAGE;
      const session = await start(cfg, signal, { prompt: 'Wait quietly for the user. Do not say any greeting. Only listen to the supplied speech.', textOnly: false, language: playerLanguage });
      try {
        await session.ready; signal.throwIfAborted();
        if (session.inputFormat !== 'pcm_16000') throw new Error('Agent-Eingabeformat muss pcm_16000 sein.');
        for (let i = 0; i < pcm.length; i += 4096) { signal.throwIfAborted(); session.send({ user_audio_chunk: pcm.subarray(i, i + 4096).toString('base64') }); }
        session.send({ user_audio_chunk: Buffer.alloc(16000 * 2).toString('base64') });
        session.startSilentInput();
        const text = await session.transcript; signal.throwIfAborted(); return { text, language_code: playerLanguage };
      } finally { session.close(); }
    },
  };
}
