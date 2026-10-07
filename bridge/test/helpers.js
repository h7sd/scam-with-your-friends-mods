import assert from 'node:assert/strict';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';
import { WebSocketServer } from 'ws';
import { createBridge } from '../src/server.js';

export const dummyConfig = {
  ELEVENLABS_API_KEY: 'test-eleven-secret-do-not-return', ELEVENLABS_VOICE_ID: 'test-voice',
  ELEVENLABS_AGENT_ID: 'agent_test', ELEVENLABS_GAME_TOOL_ID: 'tool_test', ELEVENLABS_LANGUAGE: 'de',
  REQUEST_TIMEOUT_SECONDS: 5, PORT: 8765,
};

export const callerSchema = {
  type: 'object', properties: {
    dialogue: { type: 'string' }, trust_percent: { type: 'integer', minimum: 0, maximum: 100 },
    emotion: { type: 'string', enum: ['NEUTRAL', 'ANGRY'] },
  }, required: ['dialogue', 'trust_percent', 'emotion'], additionalProperties: false,
};

export const callerRequest = (extra = {}) => ({
  messages: [{ role: 'system', content: 'Fictional game caller.' }, { role: 'user', content: 'Hello.' }],
  response_format: { type: 'json_schema', json_schema: { name: 'caller', strict: true, schema: callerSchema } },
  ...extra,
});

export function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

export async function within(promise, message = 'operation timed out', milliseconds = 2000) {
  const abort = new AbortController();
  try {
    return await Promise.race([promise, delay(milliseconds, undefined, { signal: abort.signal }).then(() => { throw new Error(message); })]);
  } finally { abort.abort(); }
}

export async function startBridge(t, deps = {}, overrides = {}) {
  const server = createBridge({ ...dummyConfig, ...overrides }, {
    provider: {
      complete: async () => ({ content: '{"dialogue":"Hallo!","trust_percent":42,"emotion":"NEUTRAL"}', audio: null, metrics: {} }),
      speak: async () => { throw new Error('Unexpected speech generation'); },
      transcribe: async () => ({ text: 'Hallo' }),
    },
    saveConfig: async () => {},
    ...deps,
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(async () => {
    const closed = once(server, 'close');
    server.close();
    server.closeAllConnections();
    await closed;
  });
  const base = `http://127.0.0.1:${server.address().port}`;
  return { server, base, post: (route, body, init = {}) => fetch(base + route, {
    ...init,
    method: 'POST', headers: { 'Content-Type': 'application/json', ...init.headers },
    body: JSON.stringify(body),
  }) };
}

export async function startVoiceServer(t, onConnection) {
  const server = new WebSocketServer({ host: '127.0.0.1', port: 0 });
  await once(server, 'listening');
  server.on('connection', onConnection);
  t.after(async () => {
    for (const socket of server.clients) socket.terminate();
    await new Promise(resolve => server.close(resolve));
  });
  return `ws://127.0.0.1:${server.address().port}`;
}

export function assertNoSecrets(value, cfg = dummyConfig) {
  const serialized = typeof value === 'string' ? value : JSON.stringify(value);
  assert.ok(!serialized.includes(cfg.ELEVENLABS_API_KEY), 'ElevenLabs key must remain private');
}

export function sendMetadata(socket, input = 'pcm_16000', output = 'pcm_24000') {
  socket.send(JSON.stringify({ type: 'conversation_initiation_metadata', conversation_initiation_metadata_event: {
    conversation_id: 'conv_test', user_input_audio_format: input, agent_output_audio_format: output,
  } }));
}

export function sendState(socket, parameters = { trust_percent: 42, emotion: 'NEUTRAL' }, toolName = 'submit_game_turn') {
  socket.send(JSON.stringify({ type: 'client_tool_call', client_tool_call: {
    tool_name: toolName, tool_call_id: 'call_test', expects_response: true, parameters,
  } }));
}

export function sendText(socket, text = 'Hallo!') {
  socket.send(JSON.stringify({ type: 'agent_response', agent_response_event: { agent_response: text } }));
}

export function sendAudio(socket, chunk) {
  socket.send(JSON.stringify({ type: 'audio', audio_event: { audio_base_64: chunk.toString('base64'), event_id: 1 } }));
}

export function sendComplete(socket) {
  socket.send(JSON.stringify({ type: 'agent_response_complete', agent_response_complete_event: { event_id: 1 } }));
}
