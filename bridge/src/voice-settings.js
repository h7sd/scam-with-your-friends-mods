// Enable only the per-conversation TTS fields used by character voices.
// Authentication, tools, LLM, call limits and the agent's base voice stay intact.
export const voiceOverrideFields = ['voice_id', 'model_id', 'stability', 'similarity_boost'];

export async function ensureVoiceOverrides(cfg, { fetchImpl = fetch, signal } = {}) {
  const key = cfg.ELEVENLABS_API_KEY?.trim(), agentId = cfg.ELEVENLABS_AGENT_ID?.trim();
  if (!key || !agentId) throw new Error('ElevenLabs-API-Schlüssel oder Agent-ID fehlt.');
  const url = `https://api.elevenlabs.io/v1/convai/agents/${encodeURIComponent(agentId)}`;
  signal?.throwIfAborted();
  const current = await fetchImpl(url, { method: 'GET', headers: { 'xi-api-key': key }, signal });
  if (!current.ok) throw new Error(`ElevenLabs-Agent konnte nicht gelesen werden (HTTP ${current.status}).`);
  let agent;
  try { agent = await current.json(); }
  catch { throw new Error('ElevenLabs hat keine gültige Agent-Konfiguration geliefert.'); }
  signal?.throwIfAborted();
  const overrides = agent.platform_settings?.overrides ?? {};
  const configuration = overrides.conversation_config_override ?? {};
  const tts = configuration.tts ?? {};
  if (voiceOverrideFields.every(field => tts[field] === true)) return { agentId, changed: false };
  const payload = {
    platform_settings: {
      overrides: {
        ...overrides,
        conversation_config_override: {
          ...configuration,
          tts: { ...tts, ...Object.fromEntries(voiceOverrideFields.map(field => [field, true])) },
        },
      },
    },
  };
  const response = await fetchImpl(url, {
    method: 'PATCH', signal,
    headers: { 'xi-api-key': key, 'Content-Type': 'application/json' }, body: JSON.stringify(payload),
  });
  if (!response.ok) throw new Error(`ElevenLabs-Stimmen-Overrides konnten nicht aktiviert werden (HTTP ${response.status}). Agent-Schreibrechte prüfen.`);
  // Once the service confirms this PATCH, report the known successful mutation
  // even if a cancellation arrived after the response. Never retry it implicitly.
  return { agentId, changed: true };
}
