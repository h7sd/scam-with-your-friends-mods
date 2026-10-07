import test from 'node:test';
import assert from 'node:assert/strict';
import { ensureVoiceOverrides, voiceOverrideFields } from '../src/voice-settings.js';
import { dummyConfig, assertNoSecrets } from './helpers.js';

test('voice permissions patch only four TTS flags while preserving existing overrides', async () => {
  const calls = [];
  const overrides = { custom_llm_extra_body: false, conversation_config_override: {
    agent: { language: true, first_message: true, prompt: { prompt: true, llm: false, tool_ids: true } },
    conversation: { text_only: true }, tts: { speed: false, pronunciation_dictionary_locators: true },
  } };
  const result = await ensureVoiceOverrides(dummyConfig, { fetchImpl: async (url, options) => {
    calls.push({ url, ...options });
    return { ok: true, json: async () => ({ platform_settings: { auth: { enable_auth: true }, overrides } }) };
  } });
  assert.deepEqual(result, { agentId: dummyConfig.ELEVENLABS_AGENT_ID, changed: true });
  assert.equal(calls.length, 2); assert.equal(calls[1].method, 'PATCH');
  const patch = JSON.parse(calls[1].body);
  assert.deepEqual(Object.keys(patch), ['platform_settings']);
  assert.deepEqual(Object.keys(patch.platform_settings), ['overrides']);
  assert.deepEqual(patch.platform_settings.overrides.conversation_config_override.agent, overrides.conversation_config_override.agent);
  assert.deepEqual(patch.platform_settings.overrides.conversation_config_override.conversation, { text_only: true });
  assert.equal(patch.platform_settings.overrides.conversation_config_override.tts.speed, false);
  assert.equal(patch.platform_settings.overrides.conversation_config_override.tts.pronunciation_dictionary_locators, true);
  for (const field of voiceOverrideFields) assert.equal(patch.platform_settings.overrides.conversation_config_override.tts[field], true);
  assertNoSecrets(patch);
  assert.ok(!JSON.stringify(patch).includes('auth')); assert.ok(!Object.hasOwn(patch, 'conversation_config'));
});

test('already enabled voice permissions do not make a remote write', async () => {
  let calls = 0;
  const result = await ensureVoiceOverrides(dummyConfig, { fetchImpl: async () => {
    calls++; return { ok: true, json: async () => ({ platform_settings: { overrides: { conversation_config_override: { tts: Object.fromEntries(voiceOverrideFields.map(field => [field, true])) } } } }) };
  } });
  assert.deepEqual(result, { agentId: dummyConfig.ELEVENLABS_AGENT_ID, changed: false }); assert.equal(calls, 1);
});

test('failed read, failed write and cancellation never silently retry settings mutations', async () => {
  await assert.rejects(ensureVoiceOverrides(dummyConfig, { fetchImpl: async () => ({ ok: false, status: 401 }) }), /401/);
  let calls = 0;
  await assert.rejects(ensureVoiceOverrides(dummyConfig, { fetchImpl: async () => {
    calls++; return calls === 1 ? { ok: true, json: async () => ({}) } : { ok: false, status: 403 };
  } }), /403/);
  assert.equal(calls, 2);
  const controller = new AbortController(); calls = 0;
  await assert.rejects(ensureVoiceOverrides(dummyConfig, { signal: controller.signal, fetchImpl: async () => {
    calls++; return { ok: true, json: async () => { controller.abort(new Error('cancel settings')); return {}; } };
  } }), /cancel settings/);
  assert.equal(calls, 1);
});
