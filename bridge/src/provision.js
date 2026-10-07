import { readFile } from 'node:fs/promises';

// Payload fields follow https://api.elevenlabs.io/openapi.json. Session overrides
// accept tool_ids, not inline tool definitions. Tools are created once per agent.
const templatePromise = readFile(new URL('../agent-template.json', import.meta.url), 'utf8').then(JSON.parse);
const apiRoot = 'https://api.elevenlabs.io/v1/convai';

function apiKey(cfg) {
  const key = cfg.ELEVENLABS_API_KEY?.trim();
  if (!key) throw new Error('ElevenLabs-API-Schlüssel fehlt.');
  return key;
}

async function request(cfg, route, { fetchImpl, signal, method = 'GET', body }) {
  signal?.throwIfAborted();
  const response = await fetchImpl(apiRoot + route, {
    method, signal,
    headers: { 'xi-api-key': apiKey(cfg), ...(body ? { 'Content-Type': 'application/json' } : {}) },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  if (!response.ok) {
    // Service error bodies can contain credentials or tenant data. Keep the
    // public error independent from their contents.
    throw new Error(`ElevenLabs-Anfrage fehlgeschlagen (HTTP ${response.status}). API-Rechte und Agent-Einstellungen prüfen.`);
  }
  let data;
  try { data = await response.json(); }
  catch { throw new Error('ElevenLabs hat keine gültige JSON-Antwort geliefert.'); }
  // A successful POST may have created a remote resource before cancellation.
  // Preserve its ID for the caller; GET operations remain fully abortable.
  if (method === 'GET') signal?.throwIfAborted();
  return data;
}

export async function buildAgentPayload(cfg, toolId) {
  const template = await templatePromise;
  const body = structuredClone(template.agent);
  const voiceId = cfg.ELEVENLABS_VOICE_ID?.trim();
  if (!voiceId) throw new Error('Für den neuen Agent fehlt die ElevenLabs-Voice-ID.');
  if (typeof toolId !== 'string' || !toolId.trim()) throw new Error('Game-Tool-ID fehlt.');
  body.conversation_config.tts.voice_id = voiceId;
  body.conversation_config.tts.model_id = cfg.ELEVENLABS_TTS_MODEL || 'eleven_v4_turbo';
  body.conversation_config.agent.language = cfg.ELEVENLABS_LANGUAGE || 'de';
  body.conversation_config.agent.prompt.tool_ids = [toolId];
  return body;
}

export async function provisionAgent(cfg, { fetchImpl = fetch, signal } = {}) {
  apiKey(cfg);
  // Validate before creating any remote resource.
  await buildAgentPayload(cfg, 'validation-only');
  const template = await templatePromise;
  const tool = await request(cfg, '/tools', {
    fetchImpl, signal, method: 'POST', body: structuredClone(template.tool),
  });
  if (typeof tool.id !== 'string' || !tool.id.trim()) throw new Error('ElevenLabs hat keine Game-Tool-ID geliefert.');
  const body = await buildAgentPayload(cfg, tool.id);
  let agent;
  try { agent = await request(cfg, '/agents/create', { fetchImpl, signal, method: 'POST', body }); }
  catch (error) {
    const failure = new Error(`${error.message} Das bereits erstellte Tool ${tool.id} bleibt im ElevenLabs-Dashboard verfügbar. Nicht automatisch erneut anlegen; zuerst das Dashboard prüfen.`, { cause: error });
    failure.toolId = tool.id;
    failure.createdResources = { toolId: tool.id };
    throw failure;
  }
  if (typeof agent.agent_id !== 'string' || !agent.agent_id.trim()) {
    const failure = new Error('ElevenLabs hat keine Agent-ID geliefert. Die Erstellung kann bereits erfolgt sein; vor einem erneuten Versuch das Dashboard prüfen.');
    failure.toolId = tool.id;
    failure.createdResources = { toolId: tool.id };
    throw failure;
  }
  return { agentId: agent.agent_id, toolId: tool.id };
}

// Return a deliberately limited diagnostic view, never arbitrary agent config:
// existing agents can contain webhook credentials and other private values.
export async function readAgentConfiguration(cfg, { fetchImpl = fetch, signal } = {}) {
  const id = cfg.ELEVENLABS_AGENT_ID?.trim();
  if (!id) throw new Error('ElevenLabs-Agent-ID fehlt.');
  const agent = await request(cfg, `/agents/${encodeURIComponent(id)}`, { fetchImpl, signal });
  const conversation = agent.conversation_config ?? {};
  const permissions = agent.platform_settings?.overrides?.conversation_config_override ?? {};
  const events = conversation.conversation?.client_events ?? [];
  const problems = [];
  for (const [enabled, label] of [
    [permissions.agent?.prompt?.prompt, 'System-Prompt-Override'],
    [permissions.agent?.first_message, 'First-Message-Override'],
    [permissions.agent?.language, 'Language-Override'],
    [permissions.agent?.prompt?.tool_ids, 'Tools-Override'],
    [permissions.conversation?.text_only, 'Text-only-Override'],
  ]) if (!enabled) problems.push(`${label} in den Agent-Sicherheitseinstellungen aktivieren.`);
  const voiceOverridesReady = ['voice_id','model_id','stability','similarity_boost'].every(field => permissions.tts?.[field] === true);
  if (!voiceOverridesReady) problems.push('Charakter-Stimmen auf der lokalen Einrichtungsseite aktivieren.');
  for (const name of ['audio', 'agent_response', 'client_tool_call', 'agent_response_complete', 'user_transcript']) {
    if (!events.includes(name)) problems.push(`Client-Event ${name} aktivieren.`);
  }
  if (conversation.tts?.agent_output_audio_format !== 'pcm_24000') problems.push('Agent-Audioformat auf pcm_24000 setzen.');
  if (conversation.asr?.user_input_audio_format !== 'pcm_16000') problems.push('Mikrofon-Audioformat auf pcm_16000 setzen.');
  const configuredToolId = cfg.ELEVENLABS_GAME_TOOL_ID?.trim();
  const candidateIds = configuredToolId ? [configuredToolId] : (conversation.agent?.prompt?.tool_ids ?? []).slice(0, 8);
  let gameToolId = null;
  for (const toolId of candidateIds) {
    const tool = await request(cfg, `/tools/${encodeURIComponent(toolId)}`, { fetchImpl, signal });
    const definition = tool.tool_config ?? {};
    const parameters = definition.parameters ?? {};
    if (definition.type === 'client' && definition.name === 'submit_game_turn' && definition.expects_response === true &&
      parameters.properties?.trust_percent?.type === 'integer' && parameters.properties?.emotion?.type === 'string' &&
      ['trust_percent', 'emotion'].every(name => parameters.required?.includes(name)) &&
      ['TRUSTING', 'SUSPICIOUS', 'ANGRY', 'NEUTRAL'].every(mood => parameters.properties.emotion.enum?.includes(mood))) {
      gameToolId = toolId; break;
    }
  }
  if (!gameToolId) problems.push('Client-Tool submit_game_turn mit trust_percent, emotion und Antwortbestätigung hinzufügen.');
  return {
    agentId: id, gameToolId, name: agent.name ?? id, ready: problems.length === 0, problems, characterVoicesReady: voiceOverridesReady,
    model: conversation.agent?.prompt?.llm ?? null,
    voiceId: conversation.tts?.voice_id ?? null,
    outputAudioFormat: conversation.tts?.agent_output_audio_format ?? null,
    inputAudioFormat: conversation.asr?.user_input_audio_format ?? null,
  };
}
