import test from 'node:test';
import assert from 'node:assert/strict';
import { CallerProfiles, callerContext, genericProfile, languageCode, selectVoice } from '../src/caller-profiles.js';
import { createAgentsProvider } from '../src/agents.js';
import { AudioBuffer, SpeechCache } from '../src/speech.js';
import { normalizeConfig } from '../src/config.js';
import { dummyConfig, callerRequest, startBridge, startVoiceServer, within, deferred, sendMetadata, sendState, sendText, sendAudio, sendComplete } from './helpers.js';

const cfg = normalizeConfig(dummyConfig);
const voices = [
  { id: 'en-old-male', name: 'English mature voice', language: 'en', gender: 'male', age: 'old', description: 'Warm deep voice' },
  { id: 'en-young-male', name: 'English young voice', language: 'en', gender: 'male', age: 'young', description: 'Bright energetic voice' },
  { id: 'de-old-male', name: 'German mature voice', language: 'de', gender: 'male', age: 'old' },
  { id: 'en-young-female', name: 'English young female voice', language: 'en', gender: 'female', age: 'young' },
  { id: 'de-old-female', name: 'German mature female voice', language: 'de', gender: 'female', age: 'old' },
];
const context = (overrides = {}) => ({ caller_id: 'caller-english', caller_name: 'Fictional caller', caller_language_code: 'en', player_language_code: 'de', response_language_code: 'de', gender: 'male', age: 65, call_net_id: 'call-a', ...overrides });
const request = overrides => callerRequest({ caller_context: context(overrides) });
const profilesWith = (catalog = voices) => new CallerProfiles({ persist: false, getVoices: async () => catalog });

test('caller native language and player microphone language remain independent', async () => {
  const profiles = profilesWith();
  const profile = await profiles.resolve(request(), cfg);
  assert.equal(profile.language, 'en');
  assert.equal(profile.playerLanguage, 'de');
  assert.equal(profile.voiceId, 'en-old-male');
  assert.equal(profile.source, 'character');
  const playerMode = await profiles.resolve(request(), { ...cfg, ELEVENLABS_LANGUAGE_MODE: 'player' });
  assert.equal(playerMode.language, 'de');
  assert.equal(playerMode.voiceId, 'de-old-male');
});

test('caller language comes from explicit language metadata rather than nationality or name', async () => {
  const profiles = profilesWith();
  const body = request({ caller_id: 'unknown-language', caller_name: 'Hans from Germany', caller_language_code: '', nationality: 'German', response_language_code: 'de', gender: '' });
  const profile = await profiles.resolve(body, { ...cfg, ELEVENLABS_CALLER_FALLBACK_LANGUAGE: 'en' });
  assert.equal(profile.language, 'en');
  assert.equal(profile.source, 'fallback');
  assert.equal(callerContext(body).nativeLanguage, '');
  assert.equal(languageCode('German'), 'de');
  assert.equal(languageCode('en-US'), 'en');
  assert.equal(languageCode('French'), 'fr');
  assert.equal(languageCode('German nationality'), '');
});

test('missing caller identity uses the generic configured voice without catalog access', async () => {
  let lookups = 0;
  const profiles = new CallerProfiles({ persist: false, getVoices: async () => { lookups++; return voices; } });
  const body = { caller_context: { caller_name: 'German Name', nationality: 'German' } };
  const actual = await profiles.resolve(body, cfg);
  assert.deepEqual(actual, genericProfile(body, cfg));
  assert.equal(actual.voiceId, cfg.ELEVENLABS_VOICE_ID);
  assert.equal(actual.source, 'default');
  assert.equal(lookups, 0);
});

test('known caller IDs keep voice selection stable and age/gender metadata select fitting voices', async () => {
  const profiles = profilesWith();
  const first = await profiles.resolve(request(), cfg);
  const nextCall = await profiles.resolve(request({ call_net_id: 'call-b' }), cfg);
  assert.equal(first.voiceId, nextCall.voiceId);
  assert.equal(nextCall.voiceId, 'en-old-male');
  assert.equal(nextCall.callKey, 'call-b');
  const young = await profiles.resolve(request({ caller_id: 'caller-young', age: 24 }), cfg);
  assert.equal(young.voiceId, 'en-young-male');
  const female = await profiles.resolve(request({ caller_id: 'caller-female', age: 24, gender: 'female' }), cfg);
  assert.equal(female.voiceId, 'en-young-female');
  const sameContext = callerContext(request());
  assert.equal(selectVoice(voices, sameContext, 'en', cfg.ELEVENLABS_TTS_MODEL, '').id,
    selectVoice([...voices].reverse(), sameContext, 'en', cfg.ELEVENLABS_TTS_MODEL, '').id);
});

test('verified voice language is model-specific and an empty catalog uses the explicit fallback', () => {
  const catalog = [
    { id: 'native', name: 'Native', language: '', gender: 'male', age: 'old', verifiedLanguages: [{ language: 'de', modelId: 'eleven_multilingual_v2' }] },
    { id: 'other', name: 'Other', language: 'en', gender: 'male', age: 'old' },
  ];
  const metadata = callerContext(request());
  const matched = selectVoice(catalog, metadata, 'de', 'eleven_multilingual_v2', 'configured');
  assert.equal(matched.id, 'native'); assert.equal(matched.matchedLanguage, true);
  assert.equal(selectVoice(catalog, metadata, 'de', 'eleven_v4_turbo', 'configured').matchedLanguage, false);
  assert.deepEqual(selectVoice([], metadata, 'en', 'eleven_v4_turbo', 'configured'), { id: 'configured', name: '', matchedLanguage: false, matchedGender: false });
});

test('matching caller gender is retained when its available voice has a different reference language', () => {
  const catalog = [
    { id: 'german-male', language: 'de', gender: 'male', age: 'old', name: 'German male' },
    { id: 'french-female', language: 'fr', gender: 'female', age: 'old', name: 'French female' },
  ];
  const selected = selectVoice(catalog, callerContext(request({ gender: 'female' })), 'de', 'eleven_v4_turbo', 'configured');
  assert.equal(selected.id, 'french-female');
  assert.equal(selected.matchedGender, true);
  assert.equal(selected.matchedLanguage, false, 'reference language matching is reported honestly');
});

test('manual voice/language overrides survive later metadata and reset returns automatic selection', async () => {
  const profiles = profilesWith();
  await profiles.resolve(request(), cfg);
  await profiles.update({ caller_id: 'caller-english', language_code: 'de', voice_id: 'de-old-female' }, cfg);
  const manual = await profiles.resolve(request({ age: 20, call_net_id: 'manual-call' }), cfg);
  assert.equal(manual.language, 'de'); assert.equal(manual.voiceId, 'de-old-female'); assert.equal(manual.source, 'manual');
  await assert.rejects(profiles.update({ caller_id: 'caller-english', language_code: 'de', voice_id: 'unknown-voice' }, cfg), /Bibliothek/);
  const reset = await profiles.update({ caller_id: 'caller-english', reset: true }, cfg);
  assert.equal(reset.manualVoice, false); assert.equal(reset.manualLanguage, '');
  assert.equal(reset.voiceName, ''); assert.equal(reset.source, 'automatic');
  const automatic = await profiles.resolve(request(), cfg);
  assert.equal(automatic.language, 'en'); assert.equal(automatic.voiceId, 'en-old-male'); assert.equal(automatic.source, 'character');
});

test('catalog failure uses the configured voice and records a visible selection warning', async () => {
  const profiles = new CallerProfiles({ persist: false, getVoices: async () => { throw new Error('catalog unavailable'); } });
  const fallback = await profiles.resolve(request(), cfg);
  assert.equal(fallback.voiceId, cfg.ELEVENLABS_VOICE_ID);
  assert.equal(fallback.language, 'en');
  const [stored] = await profiles.list();
  assert.ok(stored.selectionWarning.includes('Standardstimme'));
});

test('failed catalog refresh retains same-account voices and never reuses them across API keys', async () => {
  let calls = 0;
  const profiles = new CallerProfiles({ persist: false, getVoices: async () => {
    calls++;
    if (calls === 1) return voices;
    throw new Error('catalog unavailable');
  } });
  const original = await profiles.resolve(request(), cfg);
  profiles.catalog.expires = 0;
  const stale = await profiles.resolve(request({ call_net_id: 'stale-call' }), cfg);
  assert.equal(stale.voiceId, original.voiceId);
  assert.ok((await profiles.list())[0].selectionWarning.includes('vorhandene Stimmen'));
  const rotated = await profiles.resolve(request({ call_net_id: 'new-account-call' }), { ...cfg, ELEVENLABS_API_KEY: 'different-test-key', ELEVENLABS_VOICE_ID: 'new-configured-voice' });
  assert.equal(rotated.voiceId, 'new-configured-voice');
  assert.ok((await profiles.list())[0].selectionWarning.includes('Standardstimme'));
});

test('caller profile cancellation propagates instead of becoming a fallback selection', async () => {
  const controller = new AbortController();
  const profiles = new CallerProfiles({ persist: false, getVoices: async () => {
    controller.abort(new Error('profile cancelled'));
    throw controller.signal.reason;
  } });
  await assert.rejects(profiles.resolve(request(), cfg, controller.signal), /profile cancelled/);
  assert.deepEqual(await profiles.list(), []);
});

test('a hung catalog uses its shorter deadline without cancelling the caller request', async () => {
  const controller = new AbortController(); let catalogSignal;
  const profiles = new CallerProfiles({ persist: false, catalogTimeoutMs: 20, getVoices: async (_cfg, { signal }) => {
    catalogSignal = signal;
    return new Promise(() => {}); // A stalled transport that ignores cancellation.
  } });
  const profile = await within(profiles.resolve(request(), cfg, controller.signal));
  assert.equal(controller.signal.aborted, false);
  assert.equal(catalogSignal.aborted, true);
  assert.equal(profile.voiceId, cfg.ELEVENLABS_VOICE_ID);
  assert.ok((await profiles.list())[0].selectionWarning.includes('Standardstimme'));
});

test('per-caller language and voice reach the Agent, overriding historical response-language instructions', async t => {
  let initial;
  const url = await startVoiceServer(t, socket => socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.type === 'conversation_initiation_client_data') { initial = message; sendMetadata(socket); }
    if (message.type === 'user_message') { sendState(socket); sendText(socket, 'Hello!'); sendAudio(socket, Buffer.from([1, 0])); sendComplete(socket); }
  }));
  const body = request();
  body.messages[0].content = 'Historic instruction: Antworte auf Deutsch.';
  body._speech_profile = await profilesWith().resolve(body, cfg);
  const provider = createAgentsProvider({ getUrl: async () => url });
  const result = await within(provider.complete(body, cfg, new AbortController().signal));
  assert.equal(initial.conversation_config_override.agent.language, 'en');
  assert.equal(initial.conversation_config_override.tts.voice_id, 'en-old-male');
  assert.equal(initial.conversation_config_override.tts.model_id, 'eleven_v4_turbo');
  assert.ok(!Object.hasOwn(initial.conversation_config_override.tts, 'speed'));
  const prompt = initial.conversation_config_override.agent.prompt.prompt;
  assert.ok(prompt.indexOf('The spoken language for THIS caller is en') > prompt.indexOf('Antworte auf Deutsch'));
  assert.ok(prompt.includes("The player's microphone language is de; it does not change the caller's language"));
  assert.equal(JSON.parse(result.content).dialogue, 'Hello!');
  assert.equal(result.metrics.callerLanguage, 'en');
  await within(result.audio.done);
});

test('profile metrics capture first audio even when dialogue is returned before the voice starts', async t => {
  let peer;
  const url = await startVoiceServer(t, socket => {
    peer = socket;
    socket.on('message', raw => {
      const message = JSON.parse(raw.toString());
      if (message.type === 'conversation_initiation_client_data') sendMetadata(socket);
      if (message.type === 'user_message') { sendState(socket); sendText(socket, 'Hello!'); }
    });
  });
  const body = request(); body._speech_profile = await profilesWith().resolve(body, cfg);
  const provider = createAgentsProvider({ getUrl: async () => url });
  const result = await within(provider.complete(body, cfg, new AbortController().signal));
  t.after(() => result.audio.fail(new Error('test cleanup')));
  assert.equal(result.metrics.firstAudioMs, null);
  const received = deferred();
  result.audio.subscribe(received.resolve);
  sendAudio(peer, Buffer.from([1, 0]));
  await within(received.promise);
  assert.equal(typeof result.metrics.firstAudioMs, 'number');
  sendComplete(peer); await within(result.audio.done);
});

test('German player transcription ignores the English caller voice profile', async t => {
  let initial;
  const url = await startVoiceServer(t, socket => socket.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.type === 'conversation_initiation_client_data') { initial = message; sendMetadata(socket); }
    if (message.user_audio_chunk) socket.send(JSON.stringify({ type: 'user_transcript', user_transcription_event: { user_transcript: 'Hallo, wie geht es?' } }));
  }));
  const provider = createAgentsProvider({ getUrl: async () => url });
  const result = await within(provider.transcribe({ sample_rate: 16000, audio_base64: 'AQACAA==', caller_context: context(), _speech_profile: { language: 'en', voiceId: 'en-old-male' } }, cfg, new AbortController().signal));
  assert.equal(initial.conversation_config_override.agent.language, 'de');
  assert.ok(!Object.hasOwn(initial.conversation_config_override, 'tts'));
  assert.equal(result.language_code, 'de'); assert.equal(result.text, 'Hallo, wie geht es?');
});

test('speech cache separates identical greetings by caller, call, voice, language and model', () => {
  const cache = new SpeechCache();
  const profile = { callerId: 'a', callKey: 'call-a', voiceId: 'voice-a', language: 'en', model: 'eleven_v4_turbo' };
  const original = cache.key('Hello', cfg, profile);
  for (const changed of [{ callerId: 'b' }, { callKey: 'call-b' }, { voiceId: 'voice-b' }, { language: 'de' }, { model: 'eleven_multilingual_v2' }]) {
    assert.notEqual(cache.key('Hello', cfg, { ...profile, ...changed }), original);
  }
  cache.close();
});

test('concurrent identical greetings use each caller and call audio without another generation', async t => {
  const profiles = profilesWith();
  const generated = new Map(); let generations = 0, speechMisses = 0;
  const { post } = await startBridge(t, { profiles, provider: {
    complete: async body => {
      generations++;
      const profile = body._speech_profile;
      const marker = profile.callerId === 'caller-female' ? 2 : profile.callKey === 'call-second' ? 3 : 1;
      const pcm = Buffer.from([marker, 0]), audio = new AudioBuffer(); audio.push(pcm); audio.finish();
      generated.set(profile.callerId + '/' + profile.callKey, { profile, pcm });
      return { content: '{"dialogue":"Hello!","trust_percent":42,"emotion":"NEUTRAL"}', audio, metrics: {} };
    },
    speak: async () => { speechMisses++; throw new Error('Expected prefetched audio'); },
  } });
  const bodies = [request(), request({ caller_id: 'caller-female', gender: 'female', age: 24, call_net_id: 'call-female' }), request({ call_net_id: 'call-second' })];
  const chats = await Promise.all(bodies.map(body => post('/v1/chat/completions', body)));
  for (const chat of chats) { assert.equal(chat.status, 200); await chat.json(); }
  const replies = await Promise.all([...bodies].reverse().map(async body => {
    const response = await post('/v1/audio/speech', { input: 'Hello!', response_format: 'pcm', caller_context: body.caller_context });
    assert.equal(response.status, 200);
    return { body, bytes: Buffer.from(await response.arrayBuffer()) };
  }));
  for (const { body, bytes } of replies) assert.deepEqual(bytes, generated.get(body.caller_context.caller_id + '/' + body.caller_context.call_net_id).pcm);
  assert.equal(generations, 3); assert.equal(speechMisses, 0);
  assert.equal(generated.get('caller-english/call-a').profile.voiceId, 'en-old-male');
  assert.equal(generated.get('caller-female/call-female').profile.voiceId, 'en-young-female');
});

test('voice preview leaves caller assignments and live cached speech untouched', async t => {
  const profiles = profilesWith(), synthesis = [];
  const greeting = 'Hello, who am I speaking with?';
  const livePcm = Buffer.from([1, 0, 2, 0]), previewPcm = Buffer.from([9, 0, 10, 0]);
  const provider = {
    complete: async () => {
      const audio = new AudioBuffer(); audio.push(livePcm); audio.finish();
      return { content: JSON.stringify({ dialogue: greeting, trust_percent: 42, emotion: 'NEUTRAL' }), audio, metrics: {} };
    },
    speak: async (text, _cfg, _signal, profile) => {
      synthesis.push({ text, profile });
      const audio = new AudioBuffer(); audio.push(previewPcm); audio.finish(); return audio;
    },
  };
  const { post } = await startBridge(t, { profiles, provider });
  const body = request();
  const completion = await post('/v1/chat/completions', body);
  assert.equal(completion.status, 200); await completion.json();
  const before = structuredClone(await profiles.list());
  assert.equal(before[0].voiceId, 'en-old-male');
  assert.equal(before[0].manualVoice, false);

  // The preview says the exact same words as the pending live call, but with an
  // alternate voice. This catches accidental cache consumption and assignment.
  const preview = await post('/api/preview-voice', { caller_id: 'caller-english', voice_id: 'en-young-male', language_code: 'en' });
  assert.equal(preview.status, 200);
  assert.equal(preview.headers.get('content-type'), 'audio/wav');
  const wav = Buffer.from(await preview.arrayBuffer());
  assert.equal(wav.toString('ascii', 0, 4), 'RIFF');
  assert.deepEqual(wav.subarray(44), previewPcm);
  assert.equal(synthesis.length, 1);
  assert.equal(synthesis[0].text, greeting);
  assert.equal(synthesis[0].profile.voiceId, 'en-young-male');
  assert.equal(synthesis[0].profile.language, 'en');
  assert.deepEqual(await profiles.list(), before, 'a preview must not save a manual caller choice');

  const live = await post('/v1/audio/speech', { input: greeting, response_format: 'pcm', caller_context: body.caller_context });
  assert.equal(live.status, 200);
  assert.deepEqual(Buffer.from(await live.arrayBuffer()), livePcm);
  assert.equal(synthesis.length, 1, 'the pending call must retain its prefetched audio');
  assert.deepEqual(await profiles.list(), before);
});

test('voice preview rejects unrecognized callers, unavailable voices and invalid languages before synthesis', async t => {
  const profiles = profilesWith();
  await profiles.resolve(request(), cfg);
  const before = structuredClone(await profiles.list());
  let synthesisCalls = 0;
  const { post } = await startBridge(t, { profiles, provider: { speak: async () => { synthesisCalls++; throw new Error('Should never synthesize invalid preview'); } } });
  const valid = { caller_id: 'caller-english', voice_id: 'en-old-male', language_code: 'en' };
  for (const invalid of [
    { ...valid, caller_id: 'never-seen-in-game' }, { ...valid, caller_id: '' },
    { ...valid, voice_id: 'not-in-library' }, { ...valid, voice_id: '' },
    { ...valid, language_code: 'invalid language' }, { ...valid, language_code: '' },
    { ...valid, language_code: 'pl' }, // Valid language, but no supported preview phrase.
  ]) {
    const response = await post('/api/preview-voice', invalid);
    assert.equal(response.status, 400);
    assert.ok((await response.json()).error.message);
  }
  assert.equal(synthesisCalls, 0);
  assert.deepEqual(await profiles.list(), before);
});
