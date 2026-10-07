import test from 'node:test';
import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import { AgentSession, createAgentsProvider, signedUrl, validState } from '../src/agents.js';
import { dummyConfig, callerRequest, startVoiceServer, deferred, within, sendMetadata, sendState, sendText, sendAudio, sendComplete } from './helpers.js';

test('signed URL uses the server key and refuses auth failures or unexpected hosts', async () => {
  const controller = new AbortController();
  let request;
  const url = await signedUrl(dummyConfig, controller.signal, async (url, options) => {
    request = { url, options }; return { ok: true, json: async () => ({ signed_url: 'wss://api.elevenlabs.io/v1/convai/conversation?conversation_signature=test' }) };
  });
  assert.ok(url.startsWith('wss://api.elevenlabs.io/'));
  assert.equal(request.options.headers['xi-api-key'], dummyConfig.ELEVENLABS_API_KEY);
  assert.equal(new URL(request.url).searchParams.get('agent_id'), dummyConfig.ELEVENLABS_AGENT_ID);
  await assert.rejects(signedUrl(dummyConfig, controller.signal, async () => ({ ok: false, status: 401 })), /401/);
  for (const signed_url of ['ws://api.elevenlabs.io/x', 'wss://untrusted.example/x']) {
    await assert.rejects(signedUrl(dummyConfig, controller.signal, async () => ({ ok: true, json: async () => ({ signed_url }) })), /Ungültige/);
  }
});

test('valid game metadata requires integer range and known emotion', () => {
  assert.ok(validState({ trust_percent: 0, emotion: 'ANGRY' }));
  assert.ok(validState({ trust_percent: 100, emotion: 'TRUSTING' }));
  for (const value of [null, {}, { trust_percent: -1, emotion: 'NEUTRAL' }, { trust_percent: 101, emotion: 'NEUTRAL' },
    { trust_percent: 2.1, emotion: 'NEUTRAL' }, { trust_percent: '42', emotion: 'NEUTRAL' }, { trust_percent: 42, emotion: 'unknown' }]) assert.ok(!validState(value));
});

test('caller text and tool resolve while streamed audio remains open, with documented init fields', async t => {
  const ack = deferred(), init = deferred(); let peer;
  const first = Buffer.from([1, 0]), second = Buffer.from([2, 0]);
  const url = await startVoiceServer(t, socket => {
    peer = socket;
    socket.on('message', raw => {
      const message = JSON.parse(raw.toString());
      if (message.type === 'conversation_initiation_client_data') { init.resolve(message); sendMetadata(socket); }
      if (message.type === 'user_message') { sendState(socket); sendAudio(socket, first); sendText(socket); }
      if (message.type === 'client_tool_result') ack.resolve(message);
    });
  });
  const provider = createAgentsProvider({ getUrl: async () => url });
  const result = await within(provider.complete(callerRequest(), dummyConfig, new AbortController().signal));
  t.after(() => result.audio.fail(new Error('test cleanup')));
  assert.deepEqual(JSON.parse(result.content), { dialogue: 'Hallo!', trust_percent: 42, emotion: 'NEUTRAL' });
  assert.equal(result.audio.finished, false);
  assert.deepEqual(result.audio.chunks, [first]);
  const initial = await init.promise;
  assert.deepEqual(Object.keys(initial.conversation_config_override.conversation), ['text_only']);
  assert.deepEqual(initial.conversation_config_override.agent.prompt.tool_ids, ['tool_test']);
  assert.equal(initial.conversation_config_override.agent.first_message, '');
  assert.equal((await within(ack.promise)).is_error, false);
  sendAudio(peer, second); sendComplete(peer);
  assert.deepEqual(await within(result.audio.done), Buffer.concat([first, second]));
});

test('text-only analysis disables caller tools and returns JSON without voice', async t => {
  let initial;
  const url = await startVoiceServer(t, socket => socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.type === 'conversation_initiation_client_data') { initial = message; sendMetadata(socket); }
    if (message.type === 'user_message') sendText(socket, '{"success":true}');
  }));
  const provider = createAgentsProvider({ getUrl: async () => url });
  const result = await within(provider.complete({ messages: [{ role: 'user', content: 'Analyze objective.' }], response_format: { type: 'json_object' } }, dummyConfig, new AbortController().signal));
  assert.equal(result.content, '{"success":true}');
  assert.equal(result.audio, null);
  assert.equal(initial.conversation_config_override.conversation.text_only, true);
  assert.deepEqual(initial.conversation_config_override.agent.prompt.tool_ids, []);
});

test('unconfigured, missing or unknown metadata tools cannot produce an accepted caller turn', async t => {
  assert.throws(() => new AgentSession({ ...dummyConfig, ELEVENLABS_GAME_TOOL_ID: '' }, new AbortController().signal, { gameTool: true }), /Spielstatus-Tool fehlt/);
  const url = await startVoiceServer(t, socket => socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
    if (message.type === 'user_message') sendState(socket, { trust_percent: 42, emotion: 'NEUTRAL' }, 'arbitrary_action');
  }));
  const provider = createAgentsProvider({ getUrl: async () => url });
  await assert.rejects(within(provider.complete(callerRequest(), dummyConfig, new AbortController().signal)), /unbekanntes|ungültiges/);
});

test('complete caller response without metadata fails instead of inventing game state', async t => {
  const url = await startVoiceServer(t, socket => socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
    if (message.type === 'user_message') { sendText(socket); sendComplete(socket); }
  }));
  const provider = createAgentsProvider({ getUrl: async () => url });
  await assert.rejects(within(provider.complete(callerRequest(), dummyConfig, new AbortController().signal)), /Spielstatus/);
});

test('application ping replies with the matching event ID', async t => {
  const pong = deferred();
  const url = await startVoiceServer(t, socket => socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.type === 'conversation_initiation_client_data') {
      sendMetadata(socket);
      socket.send(JSON.stringify({ type: 'ping', ping_event: { event_id: 123, ping_ms: 50 } }));
    }
    if (message.type === 'pong') pong.resolve(message);
  }));
  const session = new AgentSession(dummyConfig, new AbortController().signal, { url, prompt: 'Test', textOnly: true });
  t.after(() => session.fail(new Error('test cleanup')));
  assert.deepEqual(await within(pong.promise), { type: 'pong', event_id: 123 });
});

test('incompatible input or output PCM metadata is rejected', async t => {
  const outputUrl = await startVoiceServer(t, socket => socket.on('message', raw => {
    if (JSON.parse(raw.toString()).type === 'conversation_initiation_client_data') sendMetadata(socket, 'pcm_16000', 'mp3_44100_128');
  }));
  const provider = createAgentsProvider({ getUrl: async () => outputUrl });
  await assert.rejects(within(provider.complete(callerRequest(), dummyConfig, new AbortController().signal)), /pcm_24000/);
  const inputUrl = await startVoiceServer(t, socket => socket.on('message', raw => {
    if (JSON.parse(raw.toString()).type === 'conversation_initiation_client_data') sendMetadata(socket, 'pcm_24000');
  }));
  const transcriber = createAgentsProvider({ getUrl: async () => inputUrl });
  await assert.rejects(within(transcriber.transcribe({ audio_base64: 'AQACAA==', sample_rate: 16000 }, dummyConfig, new AbortController().signal)), /pcm_16000/);
});

test('abort terminates a pending Agents websocket and rejects the conversation', async t => {
  const started = deferred(), closed = deferred();
  const url = await startVoiceServer(t, socket => {
    socket.on('close', closed.resolve);
    socket.on('message', raw => {
      const message = JSON.parse(raw.toString());
      if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
      if (message.type === 'user_message') started.resolve();
    });
  });
  const controller = new AbortController(), provider = createAgentsProvider({ getUrl: async () => url });
  const result = provider.complete(callerRequest(), dummyConfig, controller.signal);
  await within(started.promise); controller.abort(new Error('test cancellation'));
  await assert.rejects(within(result), /test cancellation/);
  await within(closed.promise);
});

test('short speech keeps sending paced silence until the ASR minimum audio window is reached', async t => {
  const pcm = Buffer.alloc(16000, 3); // Half a second of speech.
  const uploaded = [], laterSilenceTimes = [], closed = deferred();
  let totalBytes = 0, textMessages = 0;
  const url = await startVoiceServer(t, socket => {
    socket.on('close', closed.resolve);
    socket.on('message', raw => {
      const message = JSON.parse(raw.toString());
      if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
      if (message.type === 'user_message') textMessages++;
      if (message.user_audio_chunk) {
        const chunk = Buffer.from(message.user_audio_chunk, 'base64');
        uploaded.push(chunk); totalBytes += chunk.length;
        if (uploaded.length > 5) {
          assert.deepEqual(chunk, Buffer.alloc(3200));
          laterSilenceTimes.push(performance.now());
        }
        if (totalBytes >= 64000) socket.send(JSON.stringify({ type: 'user_transcript', user_transcription_event: { user_transcript: 'Hallo.' } }));
      }
    });
  });
  const provider = createAgentsProvider({ getUrl: async () => url });
  const result = await within(provider.transcribe({ sample_rate: 16000, audio_base64: pcm.toString('base64') }, dummyConfig, new AbortController().signal));
  assert.equal(result.text, 'Hallo.');
  assert.equal(textMessages, 0, 'audio must not be duplicated as a user text message');
  assert.deepEqual(Buffer.concat(uploaded.slice(0, 4)), pcm);
  assert.deepEqual(uploaded[4], Buffer.alloc(32000));
  assert.ok(laterSilenceTimes.length >= 5, 'the one-second blob alone cannot finish short speech');
  assert.ok(laterSilenceTimes.at(-1) - laterSilenceTimes[0] >= 300, 'the tail must advance at microphone cadence');
  await within(closed.promise);
  const packetsAtClose = uploaded.length;
  await delay(220);
  assert.equal(uploaded.length, packetsAtClose, 'no audio packets after transcript closes the session');
});

test('Agents transcription waits for metadata, uploads PCM and closes on final user_transcript', async t => {
  const pcm = Buffer.alloc(4098, 3), uploaded = [], closed = deferred();
  const url = await startVoiceServer(t, socket => {
    socket.on('close', closed.resolve);
    socket.on('message', raw => {
      const message = JSON.parse(raw.toString());
      if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
      if (message.user_audio_chunk) {
        uploaded.push(Buffer.from(message.user_audio_chunk, 'base64'));
        if (uploaded.length === 3) socket.send(JSON.stringify({ type: 'user_transcript', user_transcription_event: { user_transcript: 'Guten Tag.' } }));
      }
      assert.notEqual(message.type, 'user_message', 'transcription must not inject a duplicate user text turn');
    });
  });
  const provider = createAgentsProvider({ getUrl: async () => url });
  const result = await within(provider.transcribe({ sample_rate: 16000, audio_base64: pcm.toString('base64') }, dummyConfig, new AbortController().signal));
  assert.equal(result.text, 'Guten Tag.');
  assert.deepEqual(Buffer.concat(uploaded.slice(0, 2)), pcm);
  assert.deepEqual(uploaded[2], Buffer.alloc(32000));
  await within(closed.promise);
  for (const chunk of uploaded.slice(3)) assert.deepEqual(chunk, Buffer.alloc(3200), 'following packets must be 100ms silence');
  for (const body of [{ sample_rate: 24000, audio_base64: 'AA==' }, { sample_rate: 16000, audio_base64: 'AA==' }, { sample_rate: 16000, audio_base64: '' }]) {
    await assert.rejects(provider.transcribe(body, dummyConfig, new AbortController().signal), /Mikrofon/);
  }
});

test('excessive audio rejects a pending response immediately instead of waiting for HTTP timeout', async t => {
  const url = await startVoiceServer(t, socket => socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
    if (message.type === 'user_message') {
      const chunk = Buffer.alloc(1_000_000);
      for (let i = 0; i < 6; i++) sendAudio(socket, chunk);
    }
  }));
  const provider = createAgentsProvider({ getUrl: async () => url });
  await assert.rejects(within(provider.complete(callerRequest(), dummyConfig, new AbortController().signal)), /120 Sekunden/);
});
