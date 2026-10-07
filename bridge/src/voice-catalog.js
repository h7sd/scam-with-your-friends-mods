// List existing accessible voices; this module never creates, clones or saves
// a voice. https://elevenlabs.io/docs/api-reference/voices/search
const voicesEndpoint = 'https://api.elevenlabs.io/v2/voices';
const agentsModels = new Set([
  'eleven_flash_v2', 'eleven_flash_v2_5', 'eleven_multilingual_v2',
  'eleven_v3_conversational', 'eleven_v4', 'eleven_v4_turbo',
]);

function keyFor(cfg) {
  const key = cfg.ELEVENLABS_API_KEY?.trim();
  if (!key) throw new Error('ElevenLabs-API-Schlüssel fehlt.');
  return key;
}

function clean(value, key, limit = 300) {
  if (typeof value !== 'string') return '';
  return (key ? value.split(key).join('[redacted]') : value).replace(/[\0-\x1f\x7f]/g, ' ').trim().slice(0, limit);
}

export function normalizeVoice(voice, cfg = {}) {
  if (!voice || typeof voice !== 'object' || voice.sharing?.live_moderation_enabled === true) return null;
  const key = typeof cfg.ELEVENLABS_API_KEY === 'string' ? cfg.ELEVENLABS_API_KEY.trim() : '', id = clean(voice.voice_id, key, 128);
  if (!id || id.includes('[redacted]')) return null;
  const labels = voice.labels && typeof voice.labels === 'object' ? voice.labels : {};
  const verifiedLanguages = [], seen = new Set();
  for (const item of Array.isArray(voice.verified_languages) ? voice.verified_languages : []) {
    const language = clean(item?.language, key, 80), modelId = clean(item?.model_id, key, 100);
    if (!language || !modelId) continue;
    const accent = clean(item.accent, key, 100), signature = JSON.stringify([language, modelId, accent]);
    if (seen.has(signature)) continue;
    seen.add(signature); verifiedLanguages.push({ language, accent, modelId });
  }
  // Missing labels stay unknown. Names/descriptions never determine language,
  // gender or age, and preview/sample/sharing/private fields are never returned.
  return {
    id, name: clean(voice.name, key, 200) || id,
    language: clean(labels.language, key, 80), verifiedLanguages,
    accent: clean(labels.accent, key, 100), gender: clean(labels.gender, key, 80),
    age: clean(labels.age, key, 80), description: clean(voice.description, key, 1600),
    category: clean(voice.category, key, 80),
  };
}

async function readJson(url, cfg, { fetchImpl, signal }) {
  signal?.throwIfAborted();
  const response = await fetchImpl(url, { method: 'GET', headers: { 'xi-api-key': keyFor(cfg) }, signal });
  if (!response.ok) throw new Error(`ElevenLabs-Stimmenabfrage fehlgeschlagen (HTTP ${response.status}). API-Schlüssel und Voices-Leserechte prüfen.`);
  let body;
  try { body = await response.json(); }
  catch { throw new Error('ElevenLabs hat keine gültige Stimmenliste geliefert.'); }
  signal?.throwIfAborted(); return body;
}

export async function listVoices(cfg, { fetchImpl = fetch, signal } = {}) {
  keyFor(cfg);
  const voices = new Map(), visitedTokens = new Set();
  let nextToken;
  for (let page = 0; page < 10; page++) {
    const url = new URL(voicesEndpoint);
    url.searchParams.set('page_size', '100');
    url.searchParams.set('include_total_count', 'false');
    url.searchParams.set('include_live_moderated', 'false');
    if (nextToken !== undefined) url.searchParams.set('next_page_token', nextToken);
    const body = await readJson(url.href, cfg, { fetchImpl, signal });
    if (!Array.isArray(body.voices)) throw new Error('ElevenLabs hat keine gültige Stimmenliste geliefert.');
    for (const rawVoice of body.voices) {
      const voice = normalizeVoice(rawVoice, cfg);
      if (voice && !voices.has(voice.id)) voices.set(voice.id, voice);
    }
    if (body.has_more !== true) return [...voices.values()];
    nextToken = body.next_page_token;
    if (typeof nextToken !== 'string' || !nextToken || visitedTokens.has(nextToken)) throw new Error('ElevenLabs-Stimmenliste konnte nicht vollständig geladen werden (ungültige Seitennavigation).');
    visitedTokens.add(nextToken);
  }
  throw new Error('ElevenLabs-Stimmenliste überschreitet zehn Seiten. Eine kleinere Stimmensammlung verwenden.');
}

export async function getModelCapabilities(cfg, { fetchImpl = fetch, signal } = {}) {
  const models = await readJson('https://api.elevenlabs.io/v1/models', cfg, { fetchImpl, signal });
  if (!Array.isArray(models)) throw new Error('ElevenLabs hat keine gültige Modellliste geliefert.');
  return models.filter(model => model?.can_do_text_to_speech === true && agentsModels.has(model.model_id)).map(model => ({
    id: clean(model.model_id, cfg.ELEVENLABS_API_KEY, 100),
    name: clean(model.name, cfg.ELEVENLABS_API_KEY, 200),
    languages: (Array.isArray(model.languages) ? model.languages : []).map(language => ({
      id: clean(language.language_id, cfg.ELEVENLABS_API_KEY, 80),
      name: clean(language.name, cfg.ELEVENLABS_API_KEY, 100),
    })).filter(language => language.id),
    speedSupported: !['eleven_v4', 'eleven_v4_turbo'].includes(model.model_id),
  }));
}
