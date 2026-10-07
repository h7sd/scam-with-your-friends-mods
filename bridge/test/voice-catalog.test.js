import test from 'node:test';
import assert from 'node:assert/strict';
import { listVoices, normalizeVoice, getModelCapabilities } from '../src/voice-catalog.js';
import { dummyConfig, assertNoSecrets } from './helpers.js';

test('voice catalog follows tokens, deduplicates IDs and filters unusable live-moderated voices', async () => {
  const calls = [];
  const voices = await listVoices(dummyConfig, { fetchImpl: async (url, options) => {
    calls.push({ url: new URL(url), options });
    return { ok: true, json: async () => calls.length === 1 ? {
      voices: [{ voice_id: 'a', name: 'A', labels: { language: 'de', gender: 'male', age: 'old', accent: 'german' }, category: 'premade' }],
      has_more: true, next_page_token: 'next & page', total_count: 900,
    } : { voices: [{ voice_id: 'a', name: 'Duplicate' }, { voice_id: 'b', name: 'B' }, { voice_id: 'c', sharing: { live_moderation_enabled: true } }], has_more: false } };
  } });
  assert.equal(calls.length, 2);
  assert.equal(calls[0].url.searchParams.get('page_size'), '100');
  assert.equal(calls[0].url.searchParams.get('include_total_count'), 'false');
  assert.equal(calls[0].url.searchParams.get('include_live_moderated'), 'false');
  assert.equal(calls[1].url.searchParams.get('next_page_token'), 'next & page');
  assert.equal(calls[0].options.headers['xi-api-key'], dummyConfig.ELEVENLABS_API_KEY);
  assert.deepEqual(voices.map(voice => voice.id), ['a', 'b']);
  assert.equal(voices[0].name, 'A'); assert.equal(voices[0].language, 'de'); assert.equal(voices[0].age, 'old');
});

test('catalog preserves explicit model-specific languages and never infers traits from names', () => {
  const voice = normalizeVoice({
    voice_id: 'id', name: 'Old German Male Voice', description: 'An elderly man speaking German',
    labels: {}, samples: [{ private: true }], sharing: { whitelisted_emails: ['private@example.test'] },
    verified_languages: [
      { language: 'de', model_id: 'eleven_multilingual_v2', accent: 'german', preview_url: 'private-url' },
      { language: 'de', model_id: 'eleven_multilingual_v2', accent: 'german' },
      { language: 'en', model_id: 'eleven_flash_v2_5', accent: 'american' },
    ],
  });
  assert.equal(voice.gender, ''); assert.equal(voice.age, ''); assert.equal(voice.language, '');
  assert.deepEqual(voice.verifiedLanguages, [{ language: 'de', modelId: 'eleven_multilingual_v2', accent: 'german' }, { language: 'en', modelId: 'eleven_flash_v2_5', accent: 'american' }]);
  assert.ok(!JSON.stringify(voice).includes('private'));
  assert.ok(!Object.hasOwn(voice, 'samples')); assert.ok(!Object.hasOwn(voice, 'sharing'));
});

test('voice normalization removes unexpected private fields and redacts credentials embedded in text', () => {
  const voice = normalizeVoice({ voice_id: 'id', name: dummyConfig.ELEVENLABS_API_KEY, description: dummyConfig.ELEVENLABS_API_KEY, labels: { gender: dummyConfig.ELEVENLABS_API_KEY }, api_key: dummyConfig.ELEVENLABS_API_KEY }, dummyConfig);
  assertNoSecrets(voice); assert.ok(voice.name.includes('[redacted]'));
  assert.equal(normalizeVoice({ voice_id: dummyConfig.ELEVENLABS_API_KEY }, dummyConfig), null);
});

test('missing credentials, bad auth and invalid pagination fail without exposing response bodies', async () => {
  let calls = 0;
  await assert.rejects(listVoices({ ELEVENLABS_API_KEY: '' }, { fetchImpl: async () => { calls++; } }), /Schlüssel fehlt/);
  assert.equal(calls, 0);
  await assert.rejects(listVoices(dummyConfig, { fetchImpl: async () => ({ ok: false, status: 401, json: async () => ({ secret: dummyConfig.ELEVENLABS_API_KEY }) }) }), /401/);
  for (const body of [{ voices: [] , has_more: true }, { other: [] }]) {
    await assert.rejects(listVoices(dummyConfig, { fetchImpl: async () => ({ ok: true, json: async () => body }) }), /Stimmenliste/);
  }
  await assert.rejects(listVoices(dummyConfig, { fetchImpl: async () => ({ ok: true, json: async () => ({ voices: [], has_more: true, next_page_token: 'repeated' }) }) }), /Seitennavigation/);
});

test('catalog caps pagination and honors abort before another request', async () => {
  let calls = 0;
  await assert.rejects(listVoices(dummyConfig, { fetchImpl: async () => ({ ok: true, json: async () => ({ voices: [], has_more: true, next_page_token: String(++calls) }) }) }), /zehn Seiten/);
  assert.equal(calls, 10);
  const controller = new AbortController(); calls = 0;
  await assert.rejects(listVoices(dummyConfig, { signal: controller.signal, fetchImpl: async () => {
    calls++; controller.abort(new Error('cancel catalog'));
    return { ok: true, json: async () => ({ voices: [], has_more: true, next_page_token: 'next' }) };
  } }), /cancel catalog/);
  assert.equal(calls, 1);
});

test('model discovery returns only compatible TTS models and marks v4 speed unsupported', async () => {
  const models = await getModelCapabilities(dummyConfig, { fetchImpl: async () => ({ ok: true, json: async () => [
    { model_id: 'eleven_v4_turbo', name: 'v4', can_do_text_to_speech: true, languages: [{ language_id: 'de', name: 'German' }] },
    { model_id: 'eleven_flash_v2_5', name: 'Flash', can_do_text_to_speech: true },
    { model_id: 'scribe_v2_realtime', can_do_text_to_speech: false },
    { model_id: 'unverified-new-model', can_do_text_to_speech: true },
  ] }) });
  assert.deepEqual(models.map(model => model.id), ['eleven_v4_turbo', 'eleven_flash_v2_5']);
  assert.equal(models[0].speedSupported, false); assert.equal(models[1].speedSupported, true);
  assert.deepEqual(models[0].languages, [{ id: 'de', name: 'German' }]);
});
