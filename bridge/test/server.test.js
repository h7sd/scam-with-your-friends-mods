import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { setTimeout as delay } from 'node:timers/promises';
import { createAgentsProvider } from '../src/agents.js';
import { AudioBuffer } from '../src/speech.js';
import { startBridge, dummyConfig, callerRequest, assertNoSecrets, deferred, within, startVoiceServer, sendMetadata, sendState, sendText, sendAudio, sendComplete } from './helpers.js';

test('local config/status are secret-free, password blanks preserve saved keys and the bound port', async t => {
  let saved;
  const { base, post } = await startBridge(t, { saveConfig: async cfg => { saved = cfg; } });
  const status = await (await fetch(base + '/api/status')).json();
  assert.equal(status.configured, true); assertNoSecrets(status);
  const update = await post('/api/config', { ELEVENLABS_API_KEY: '', ELEVENLABS_LANGUAGE: 'en', PORT: 9999 });
  assert.equal(update.status, 200);
  const publicUpdate = await update.json(); assertNoSecrets(publicUpdate);
  assert.equal(publicUpdate.language, 'en');
  assert.equal(saved.ELEVENLABS_API_KEY, dummyConfig.ELEVENLABS_API_KEY);
  assert.equal(saved.PORT, dummyConfig.PORT);
  const cleared = await post('/api/config', { ELEVENLABS_API_KEY: '', CLEAR_ELEVENLABS_API_KEY: true });
  assert.equal((await cleared.json()).configured, false);
  assert.equal(saved.ELEVENLABS_API_KEY, '');
});

test('Host and Origin checks block credential writes from other sites', async t => {
  let writes = 0;
  const { base, post } = await startBridge(t, { saveConfig: async () => { writes++; } });
  // Node fetch intentionally normalizes Host; use the HTTP transport to model
  // an actual DNS-rebinding request with a foreign Host header.
  const foreignHostStatus = await new Promise((resolve, reject) => {
    const request = http.get(base + '/health', { headers: { Host: 'attacker.example' } }, response => {
      response.resume(); response.on('end', () => resolve(response.statusCode));
    });
    request.on('error', reject);
  });
  assert.equal(foreignHostStatus, 403);
  const foreignOrigin = await post('/api/config', { ELEVENLABS_API_KEY: 'overwritten' }, { headers: { Origin: 'https://attacker.example' } });
  assert.equal(foreignOrigin.status, 403); assert.equal(writes, 0);
  const sameOrigin = await post('/api/config', { ELEVENLABS_LANGUAGE: 'en' }, { headers: { Origin: base } });
  assert.equal(sameOrigin.status, 200); assert.equal(writes, 1);
  const html = await fetch(base + '/');
  assert.ok(html.headers.get('content-security-policy').includes("frame-ancestors 'none'"));
  assertNoSecrets(await html.text());
});

test('backend errors redact secrets and invalid input never reaches the provider', async t => {
  let calls = 0;
  const { post } = await startBridge(t, { provider: { complete: async () => {
    calls++; throw new Error(`Authorization rejected ${dummyConfig.ELEVENLABS_API_KEY}`);
  } } });
  const malformed = await post('/v1/chat/completions', { messages: [] });
  assert.equal(malformed.status, 502); assert.equal(calls, 0);
  const failure = await post('/v1/chat/completions', callerRequest());
  assert.equal(failure.status, 502);
  const body = await failure.text(); assertNoSecrets(body); assert.ok(body.includes('[redacted]'));
});

test('OpenAI-compatible completion and SSE have matching validated schema content', async t => {
  const { post } = await startBridge(t);
  const standard = await (await post('/v1/chat/completions', callerRequest())).json();
  assert.equal(standard.object, 'chat.completion');
  assert.equal(standard.model, 'elevenlabs-agent');
  assert.equal(standard.choices[0].message.role, 'assistant');
  assert.equal(standard.choices[0].finish_reason, 'stop');
  const stream = await post('/v1/chat/completions', callerRequest({ stream: true }));
  assert.equal(stream.headers.get('content-type'), 'text/event-stream');
  const events = (await stream.text()).trim().split('\n\n').map(line => line.slice('data: '.length));
  assert.equal(events.pop(), '[DONE]');
  const [content, stop] = events.map(JSON.parse);
  assert.equal(content.object, 'chat.completion.chunk');
  assert.equal(content.choices[0].delta.content, standard.choices[0].message.content);
  assert.equal(content.choices[0].finish_reason, null);
  assert.deepEqual(stop.choices[0].delta, {});
  assert.equal(stop.choices[0].finish_reason, 'stop');
  assert.equal(content.id, stop.id);
});

test('invalid game JSON is rejected before sending SSE success', async t => {
  const { post } = await startBridge(t, { provider: { complete: async () => ({ content: '{"dialogue":"Hi","trust_percent":999,"emotion":"NEUTRAL"}', audio: null }) } });
  const response = await post('/v1/chat/completions', callerRequest({ stream: true }));
  assert.equal(response.status, 502);
  assert.ok((await response.json()).error.message.includes('Spielschema'));
});

test('schema rejection cancels a caller audio producer already detached by the provider', async t => {
  const audio = new AudioBuffer(), finished = deferred();
  audio.onFinish = finished.resolve;
  const { post } = await startBridge(t, { provider: { complete: async () => ({
    content: '{"dialogue":"Hi","trust_percent":999,"emotion":"NEUTRAL"}', audio,
  }) } });
  const response = await post('/v1/chat/completions', callerRequest());
  assert.equal(response.status, 502);
  await within(finished.promise);
  await assert.rejects(audio.done, /Spielschema/);
});

test('caller completion prefetched audio is consumed as live PCM without another Agents request', async t => {
  let peer, connections = 0, newSpeechCalls = 0;
  const first = Buffer.from([1, 0, 2, 0]), second = Buffer.from([3, 0, 4, 0]);
  const url = await startVoiceServer(t, socket => {
    connections++; peer = socket;
    socket.on('message', raw => {
      const message = JSON.parse(raw.toString());
      if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
      if (message.type === 'user_message') { sendState(socket); sendAudio(socket, first); sendText(socket); }
    });
  });
  const provider = createAgentsProvider({ getUrl: async () => url });
  provider.speak = async () => { newSpeechCalls++; throw new Error('Cached caller audio should be reused'); };
  const { post } = await startBridge(t, { provider });
  const completion = await within(post('/v1/chat/completions', callerRequest()));
  assert.equal(completion.status, 200);
  const dialogue = JSON.parse((await completion.json()).choices[0].message.content).dialogue;
  const response = await within(post('/v1/audio/speech', { input: dialogue, response_format: 'pcm' }));
  assert.equal(response.headers.get('content-type'), 'audio/pcm');
  assert.equal(response.headers.get('x-sample-rate'), '24000');
  assert.equal(response.headers.get('x-channels'), '1');
  assert.equal(response.headers.get('x-audio-format'), 's16le-mono');
  const reader = response.body.getReader();
  const initial = await within(reader.read());
  assert.equal(initial.done, false); assert.deepEqual(Buffer.from(initial.value), first);
  assert.equal(newSpeechCalls, 0); assert.equal(connections, 1);
  sendAudio(peer, second); sendComplete(peer);
  const rest = [];
  for (;;) { const chunk = await within(reader.read()); if (chunk.done) break; rest.push(Buffer.from(chunk.value)); }
  assert.deepEqual(Buffer.concat(rest), second);
});

test('caller and read-aloud PCM responses finish only after paced silent microphone frames', async t => {
  for (const caller of [true, false]) await t.test(caller ? 'cached caller audio' : 'read-aloud audio', async t => {
    const pcm = Buffer.from([10, 0, 20, 0]), closed = deferred(), framesAfterText = [];
    let peer, triggered = false, canFinish = false, finalSent = false, packetCount = 0;
    const maybeComplete = () => {
      if (!canFinish || finalSent || framesAfterText.length < 2) return;
      finalSent = true; sendComplete(peer);
    };
    const url = await startVoiceServer(t, socket => {
      peer = socket; socket.on('close', closed.resolve);
      socket.on('message', raw => {
        const message = JSON.parse(raw.toString());
        if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
        if (message.type === 'user_message') {
          triggered = true;
          if (caller) sendState(socket);
          sendAudio(socket, pcm); sendText(socket, 'Hallo!');
        }
        if (message.user_audio_chunk) {
          packetCount++;
          assert.deepEqual(Buffer.from(message.user_audio_chunk, 'base64'), Buffer.alloc(3200));
          if (triggered) framesAfterText.push(performance.now());
          maybeComplete();
        }
      });
    });
    const provider = createAgentsProvider({ getUrl: async () => url });
    const { post } = await startBridge(t, { provider });
    if (caller) {
      const chat = await within(post('/v1/chat/completions', callerRequest()));
      assert.equal(chat.status, 200);
      assert.equal(JSON.parse((await chat.json()).choices[0].message.content).dialogue, 'Hallo!');
    }
    const voice = await within(post('/v1/audio/speech', { input: 'Hallo!', response_format: 'pcm' }));
    assert.equal(voice.status, 200);
    assert.equal(voice.headers.get('content-type'), 'audio/pcm');
    const reader = voice.body.getReader();
    const first = await within(reader.read());
    assert.deepEqual(Buffer.from(first.value), pcm);
    assert.equal(finalSent, false, 'PCM must stream before the end event');
    canFinish = true; maybeComplete();
    assert.equal((await within(reader.read())).done, true, 'HTTP stream must terminate cleanly after end of turn');
    assert.ok(framesAfterText.length >= 2);
    assert.ok(framesAfterText[1] - framesAfterText[0] >= 70, 'silence frames must be paced, not burst');
    await within(closed.promise);
    const countAtClose = packetCount;
    await delay(220);
    assert.equal(packetCount, countAtClose, 'microphone packets must stop when the voice session closes');
  });
});

test('WAV fallback and transcription response preserve API formats', async t => {
  const pcm = Buffer.from([1, 0, 2, 0]);
  const { post } = await startBridge(t, { provider: {
    speak: async () => { const audio = new AudioBuffer(); audio.push(pcm); audio.finish(); return audio; },
    transcribe: async body => ({ text: body.sample_rate === 16000 ? 'Hallo Welt.' : 'Invalid' }),
  } });
  const voice = await post('/v1/audio/speech', { input: 'Hello', response_format: 'wav' });
  assert.equal(voice.headers.get('content-type'), 'audio/wav');
  const wav = Buffer.from(await voice.arrayBuffer());
  assert.equal(wav.toString('ascii', 0, 4), 'RIFF'); assert.deepEqual(wav.subarray(44), pcm);
  const transcript = await post('/v1/audio/transcriptions', { audio_base64: 'AQACAA==', sample_rate: 16000 });
  assert.deepEqual(await transcript.json(), { text: 'Hallo Welt.' });
  assert.equal((await post('/v1/audio/speech', { input: 'x', response_format: 'mp3' })).status, 400);
});

test('disconnect aborts an in-flight provider call', async t => {
  const started = deferred(), cancelled = deferred();
  const { base } = await startBridge(t, { provider: { complete: async (_body, _cfg, signal) => {
    started.resolve(); signal.addEventListener('abort', () => cancelled.resolve(signal.reason), { once: true });
    return new Promise(() => {});
  } } });
  const request = http.request(base + '/v1/chat/completions', { method: 'POST', headers: { 'Content-Type': 'application/json' } });
  request.on('error', () => {}); request.end(JSON.stringify(callerRequest()));
  await within(started.promise); request.destroy();
  assert.match((await within(cancelled.promise)).message, /Verbindung beendet/);
});

test('explicit agent creation persists both created IDs and returns no API key', async t => {
  let saved, creations = 0;
  const { post } = await startBridge(t, {
    provisionAgent: async cfg => { creations++; assert.equal(cfg.ELEVENLABS_API_KEY, dummyConfig.ELEVENLABS_API_KEY); return { agentId: 'agent_created', toolId: 'tool_created' }; },
    saveConfig: async cfg => { saved = cfg; },
  });
  const response = await post('/api/create-agent', {});
  assert.equal(response.status, 200); assert.equal(creations, 1);
  const result = await response.json(); assertNoSecrets(result);
  assert.equal(result.agentId, 'agent_created'); assert.equal(result.gameToolId, 'tool_created');
  assert.equal(saved.ELEVENLABS_AGENT_ID, result.agentId);
  assert.equal(saved.ELEVENLABS_GAME_TOOL_ID, result.gameToolId);
});
