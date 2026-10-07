import { readFile, writeFile, rename } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { bridgeRoot } from './config.js';
import { listVoices } from './voice-catalog.js';

const names = { english: 'en', german: 'de', deutsch: 'de', french: 'fr', spanish: 'es', italian: 'it', portuguese: 'pt', chinese: 'zh', mandarin: 'zh', japanese: 'ja', korean: 'ko', hindi: 'hi', russian: 'ru', polish: 'pl', arabic: 'ar', dutch: 'nl', turkish: 'tr', swedish: 'sv', norwegian: 'no', finnish: 'fi', danish: 'da', czech: 'cs', greek: 'el', ukrainian: 'uk', hungarian: 'hu', romanian: 'ro', indonesian: 'id', vietnamese: 'vi', thai: 'th', malay: 'ms', tamil: 'ta', bulgarian: 'bg', croatian: 'hr', slovak: 'sk' };
export function languageCode(value) {
  if (typeof value !== 'string') return '';
  const normalized = value.trim().toLowerCase().replace('_', '-');
  if (names[normalized]) return names[normalized];
  return /^[a-z]{2,3}(?:-[a-z]{2,4})?$/.test(normalized) ? normalized.split('-')[0] : '';
}
export function genderCode(value) { const valueLower = String(value ?? '').trim().toLowerCase(); return ['male','man','m'].includes(valueLower) ? 'male' : ['female','woman','f'].includes(valueLower) ? 'female' : ['neutral','nonbinary','non-binary','androgynous'].includes(valueLower) ? 'neutral' : ''; }
export function callerContext(body = {}) {
  const data = body.caller_context ?? {};
  const age = Number(data.age);
  return {
    id: typeof data.caller_id === 'string' ? data.caller_id.slice(0, 160) : '',
    name: typeof data.caller_name === 'string' ? data.caller_name.slice(0, 160) : '',
    nativeLanguage: languageCode(data.caller_language_code),
    responseLanguage: languageCode(data.response_language_code),
    playerLanguage: languageCode(data.player_language_code),
    gender: genderCode(data.gender), age: Number.isInteger(age) && age > 0 && age <= 120 ? age : null,
    description: typeof data.voice_description === 'string' ? data.voice_description.slice(0, 5000) : '',
    callKey: data.call_net_id ? String(data.call_net_id) : String(data.call_id ?? body.call_net_id ?? body.session_id ?? ''),
  };
}
function ageBucket(age) { return age === null ? '' : age >= 60 ? 'old' : age >= 35 ? 'middle_aged' : 'young'; }
function voiceLanguages(voice, model) {
  return new Set([languageCode(voice.language), ...(voice.verifiedLanguages ?? []).filter(entry => !entry.modelId || entry.modelId === model).map(entry => languageCode(entry.language))].filter(Boolean));
}
export function selectVoice(voices, context, language, model, fallbackId) {
  if (!voices.length) return { id: fallbackId || '', name: '', matchedLanguage: false, matchedGender: false };
  const genderMatches = context.gender ? voices.filter(voice => genderCode(voice.gender) === context.gender) : [];
  const candidates = genderMatches.length ? genderMatches : voices;
  const bucket = ageBucket(context.age);
  const ranked = candidates.map(voice => {
    const languages = voiceLanguages(voice, model), matchesLanguage = languages.has(language);
    const age = String(voice.age ?? '').toLowerCase().replace(/[ -]/g, '_');
    const canonicalAge = ['elderly','senior','older','old'].includes(age) ? 'old' : ['middle_aged','adult'].includes(age) ? 'middle_aged' : ['young','young_adult'].includes(age) ? 'young' : '';
    const styleWords = new Set(String(voice.description ?? '').toLowerCase().match(/\b(?:calm|warm|deep|raspy|soft|energetic|confident|stern|gentle|gruff|bright|authoritative)\b/g) ?? []);
    const styleOverlap = [...styleWords].filter(word => new RegExp(`\\b${word}\\b`, 'i').test(context.description)).length;
    return { voice, score: (matchesLanguage ? 100 : languages.size === 0 ? 10 : 0) + (bucket && canonicalAge === bucket ? 20 : 0) + Math.min(6, styleOverlap * 2), matchesLanguage };
  }).sort((a,b) => b.score-a.score || a.voice.id.localeCompare(b.voice.id));
  const best = ranked.filter(item => item.score >= ranked[0].score - 2);
  const hash = createHash('sha256').update(context.id || 'default').digest().readUInt32BE(0);
  const selected = best[hash % best.length];
  return { id: selected.voice.id, name: selected.voice.name, matchedLanguage: selected.matchesLanguage, matchedGender: !!context.gender && genderCode(selected.voice.gender) === context.gender };
}
export function genericProfile(body, cfg) {
  const language = languageCode(body.language_code) || languageCode(cfg.ELEVENLABS_LANGUAGE) || 'de';
  return { callerId: '', callerName: '', language, playerLanguage: languageCode(cfg.ELEVENLABS_LANGUAGE) || 'de', voiceId: cfg.ELEVENLABS_VOICE_ID || '', model: cfg.ELEVENLABS_TTS_MODEL || 'eleven_v4_turbo', callKey: '', source: 'default', matchedLanguage: false, matchedGender: false };
}
export function speechIdentity(profile) { return profile ? `${profile.callerId ?? ''}|${profile.callKey ?? ''}|${profile.voiceId ?? ''}|${profile.language ?? ''}|${profile.model ?? ''}` : ''; }

export class CallerProfiles {
  constructor({ getVoices = listVoices, destination = path.join(bridgeRoot, 'caller-profiles.json'), persist = true, catalogTimeoutMs = 4000 } = {}) {
    this.getVoices = getVoices; this.destination = destination; this.persist = persist;
    this.entries = new Map(); this.loaded = null; this.catalog = null; this.saveChain = Promise.resolve(); this.catalogTimeoutMs = catalogTimeoutMs;
  }
  async load() {
    this.loaded ??= (async () => {
      if (!this.persist) return;
      try {
        const stored = JSON.parse(await readFile(this.destination, 'utf8'));
        for (const item of stored.callers ?? []) if (typeof item.id === 'string') this.entries.set(item.id, item);
      } catch (error) { if (error.code !== 'ENOENT') throw new Error('Anruferprofile konnten nicht geladen werden.'); }
    })();
    return this.loaded;
  }
  async voices(cfg, signal) {
    const key = createHash('sha256').update(cfg.ELEVENLABS_API_KEY).digest('hex');
    if (!this.catalog || this.catalog.key !== key || this.catalog.expires < Date.now()) {
      try {
        const controller = new AbortController();
        const forwardAbort = () => controller.abort(signal.reason);
        signal?.addEventListener('abort', forwardAbort, { once: true });
        if (signal?.aborted) forwardAbort();
        const timer = setTimeout(() => controller.abort(new Error('Stimmenabfrage-Zeitlimit überschritten.')), this.catalogTimeoutMs);
        let stop, voices;
        try {
          controller.signal.throwIfAborted();
          const aborted = new Promise((_, reject) => { stop = () => reject(controller.signal.reason); controller.signal.addEventListener('abort', stop, { once: true }); });
          voices = await Promise.race([this.getVoices(cfg, { signal: controller.signal }), aborted]);
        } finally { clearTimeout(timer); signal?.removeEventListener('abort', forwardAbort); controller.signal.removeEventListener('abort', stop); }
        this.catalog = { key, voices, expires: Date.now() + 10 * 60_000 }; this.catalogWarning = '';
      } catch (error) {
        if (signal?.aborted) throw error;
        if (this.catalog?.key === key) { this.catalogWarning = 'Die Stimmenliste konnte nicht aktualisiert werden; vorhandene Stimmen werden verwendet.'; return this.catalog.voices; }
        throw error;
      }
    }
    return this.catalog.voices;
  }
  async save() {
    if (!this.persist) return;
    const snapshot = JSON.stringify({ callers: [...this.entries.values()] }, null, 2) + '\n';
    this.saveChain = this.saveChain.catch(() => {}).then(async () => { await writeFile(this.destination + '.tmp', snapshot, { mode: 0o600 }); await rename(this.destination + '.tmp', this.destination); });
    await this.saveChain;
  }
  async resolve(body, cfg, signal) {
    const context = callerContext(body);
    if (!context.id) return genericProfile(body, cfg);
    await this.load();
    const previous = this.entries.get(context.id);
    const model = cfg.ELEVENLABS_TTS_MODEL || 'eleven_v4_turbo';
    const language = languageCode(previous?.manualLanguage) || (cfg.ELEVENLABS_LANGUAGE_MODE === 'player' ? (context.playerLanguage || languageCode(cfg.ELEVENLABS_LANGUAGE)) : context.nativeLanguage) || languageCode(cfg.ELEVENLABS_CALLER_FALLBACK_LANGUAGE) || 'en';
    let voices, selectionWarning = '';
    try { voices = await this.voices(cfg, signal); selectionWarning = this.catalogWarning || ''; }
    catch (error) { if (signal?.aborted) throw error; voices = []; selectionWarning = 'Stimmenliste nicht verfügbar. Die konfigurierte Standardstimme wird verwendet; Voices-Leserechte und Verbindung prüfen.'; }
    const existingVoice = previous?.voiceId && voices.find(voice => voice.id === previous.voiceId);
    const selectionChanged = previous && (previous.language !== language || previous.model !== model || previous.gender !== context.gender || previous.age !== context.age);
    const chosen = existingVoice && (!selectionChanged || previous.manualVoice) ? { id: existingVoice.id, name: existingVoice.name, matchedLanguage: voiceLanguages(existingVoice, model).has(language), matchedGender: !!context.gender && genderCode(existingVoice.gender) === context.gender } : selectVoice(voices, context, language, model, cfg.ELEVENLABS_VOICE_ID);
    const entry = { id: context.id, name: context.name || previous?.name || context.id, nativeLanguage: context.nativeLanguage, language, gender: context.gender, age: context.age, voiceId: chosen.id, voiceName: chosen.name, model, matchedLanguage: chosen.matchedLanguage, matchedGender: chosen.matchedGender, manualLanguage: previous?.manualLanguage || '', manualVoice: previous?.manualVoice || false, source: previous?.manualLanguage ? 'manual' : cfg.ELEVENLABS_LANGUAGE_MODE === 'player' ? 'player' : context.nativeLanguage ? 'character' : 'fallback', selectionWarning };
    if (JSON.stringify(previous) !== JSON.stringify(entry)) { this.entries.set(context.id, entry); await this.save(); }
    return { callerId: context.id, callerName: entry.name, language, playerLanguage: context.playerLanguage || languageCode(cfg.ELEVENLABS_LANGUAGE) || 'de', voiceId: chosen.id, model, callKey: context.callKey, source: entry.source, matchedLanguage: chosen.matchedLanguage, matchedGender: chosen.matchedGender };
  }
  async list() { await this.load(); return [...this.entries.values()].sort((a,b) => a.name.localeCompare(b.name)); }
  async update(body, cfg, signal) {
    await this.load(); const previous = this.entries.get(body.caller_id);
    if (!previous) throw new Error('Anrufer wurde noch nicht im Spiel erkannt.');
    const updated = { ...previous };
    if (body.reset) { updated.manualLanguage = ''; updated.manualVoice = false; updated.voiceId = ''; updated.voiceName = ''; updated.source = 'automatic'; updated.language = previous.nativeLanguage || languageCode(cfg.ELEVENLABS_CALLER_FALLBACK_LANGUAGE) || 'en'; }
    else {
      const language = languageCode(body.language_code);
      if (!language) throw new Error('Ungültige Anrufer-Sprache.');
      const voice = (await this.voices(cfg, signal)).find(voice => voice.id === body.voice_id);
      if (!voice) throw new Error('Die Stimme ist nicht in deiner ElevenLabs-Bibliothek verfügbar.');
      updated.language = language; updated.manualLanguage = language; updated.voiceId = voice.id; updated.voiceName = voice.name; updated.manualVoice = true; updated.source = 'manual';
    }
    this.entries.set(previous.id, updated); await this.save(); return updated;
  }
}
