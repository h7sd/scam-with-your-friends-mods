import { createHash } from 'node:crypto';
import { speechIdentity } from './caller-profiles.js';
export const sampleRate = 24000;
export function wav(pcm) {
  if (pcm.length % 2) throw new Error('Ungültige PCM-Antwort (ungerade Bytezahl).');
  const header = Buffer.alloc(44);
  header.write('RIFF'); header.writeUInt32LE(36 + pcm.length, 4); header.write('WAVE', 8);
  header.write('fmt ', 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20);
  header.writeUInt16LE(1, 22); header.writeUInt32LE(sampleRate, 24); header.writeUInt32LE(sampleRate * 2, 28);
  header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34); header.write('data', 36); header.writeUInt32LE(pcm.length, 40);
  return Buffer.concat([header, pcm]);
}
export class AudioBuffer {
  constructor() {
    this.chunks = []; this.listeners = new Set(); this.finished = false; this.bytes = 0;
    this.done = new Promise((resolve, reject) => { this.resolve = resolve; this.reject = reject; }); this.done.catch(() => {});
  }
  push(chunk) {
    if (this.finished) return;
    this.bytes += chunk.length;
    if (this.bytes > sampleRate * 2 * 120) { this.fail(new Error('Agent-Audio überschreitet 120 Sekunden.')); return; }
    this.chunks.push(chunk); for (const listener of this.listeners) listener(chunk);
  }
  subscribe(listener) {
    for (const chunk of this.chunks) listener(chunk);
    if (!this.finished) this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }
  finish() {
    if (this.finished) return;
    this.finished = true; const pcm = Buffer.concat(this.chunks);
    if (!pcm.length || pcm.length % 2) { this.error = new Error('Agent hat kein gültiges PCM-Audio geliefert.'); this.reject(this.error); }
    else this.resolve(pcm);
    this.onFinish?.();
  }
  fail(error) { if (this.finished) return; this.finished = true; this.error = error; this.reject(error); this.onFinish?.(); }
}
export class SpeechCache {
  constructor({ createSpeech, maxEntries = 16 } = {}) { this.entries = new Map(); this.createSpeech = createSpeech; this.maxEntries = maxEntries; }
  key(text, cfg, profile) { return createHash('sha256').update(`${cfg.ELEVENLABS_API_KEY}|${cfg.ELEVENLABS_AGENT_ID}|${speechIdentity(profile)}|${text.trim()}`).digest('hex'); }
  prune() { for (const [key, entry] of this.entries) if (entry.expires < Date.now()) { entry.audio.fail(new Error('Agent-Audiopuffer abgelaufen.')); this.entries.delete(key); } }
  put(text, cfg, audio, profile) {
    this.prune(); const key = this.key(text, cfg, profile), old = this.entries.get(key);
    if (old && old.audio !== audio) old.audio.fail(new Error('Agent-Audiopuffer ersetzt.'));
    this.entries.delete(key);
    while (this.entries.size >= this.maxEntries) { const oldest = this.entries.keys().next().value; this.entries.get(oldest).audio.fail(new Error('Agent-Audiopuffer ersetzt.')); this.entries.delete(oldest); }
    this.entries.set(key, { audio, expires: Date.now() + 120_000 });
  }
  async take(text, cfg, signal, profile) {
    this.prune(); const key = this.key(text, cfg, profile), entry = this.entries.get(key); this.entries.delete(key);
    if (entry) return entry.audio;
    if (!this.createSpeech) throw new Error('Kein Agent-Audio für diesen Text verfügbar.');
    return this.createSpeech(text, cfg, signal, profile);
  }
  async audio(text, cfg, signal, profile) {
    const audio = await this.take(text, cfg, signal, profile);
    const cancel = () => audio.fail(new Error('Agent-Sprachausgabe abgebrochen.'));
    signal?.addEventListener('abort', cancel, { once: true });
    try { signal?.throwIfAborted(); return wav(await audio.done); }
    finally { signal?.removeEventListener('abort', cancel); }
  }
  close() { for (const entry of this.entries.values()) entry.audio.fail(new Error('Backend beendet.')); this.entries.clear(); }
}
