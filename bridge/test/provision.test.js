import test from 'node:test';
import assert from 'node:assert/strict';
import { buildAgentPayload, provisionAgent, readAgentConfiguration } from '../src/provision.js';
import { dummyConfig, assertNoSecrets } from './helpers.js';
import { readFile } from 'node:fs/promises';

test('provision creates one metadata client tool and Agent with exact allowed overrides', async () => {
  const calls = [];
  const created = await provisionAgent(dummyConfig, { fetchImpl: async (url, options) => {
    calls.push({ url, ...options, body: JSON.parse(options.body) });
    return { ok: true, json: async () => url.endsWith('/tools') ? { id: 'tool_created' } : { agent_id: 'agent_created' } };
  } });
  assert.deepEqual(created, { agentId: 'agent_created', toolId: 'tool_created' });
  assert.equal(calls.length, 2);
  assert.equal(calls[0].url, 'https://api.elevenlabs.io/v1/convai/tools');
  const tool = calls[0].body.tool_config;
  assert.equal(tool.type, 'client'); assert.equal(tool.name, 'submit_game_turn');
  assert.equal(tool.expects_response, true); assert.equal(tool.pre_tool_speech, 'off');
  assert.deepEqual(Object.keys(tool.parameters.properties), ['trust_percent', 'emotion']);
  assert.equal(tool.parameters.properties.trust_percent.type, 'integer');
  const body = calls[1].body;
  assert.equal(body.conversation_config.tts.voice_id, dummyConfig.ELEVENLABS_VOICE_ID);
  assert.equal(body.conversation_config.tts.agent_output_audio_format, 'pcm_24000');
  assert.deepEqual(body.conversation_config.agent.prompt.tool_ids, ['tool_created']);
  assert.equal(body.platform_settings.auth.enable_auth, true);
  const override = body.platform_settings.overrides.conversation_config_override;
  assert.equal(override.agent.prompt.prompt, true); assert.equal(override.agent.prompt.tool_ids, true);
  assert.equal(override.conversation.text_only, true);
  assert.ok(body.conversation_config.conversation.client_events.includes('agent_response_complete'));
  assertNoSecrets(body);
  assert.equal((await buildAgentPayload({ ...dummyConfig, ELEVENLABS_LANGUAGE: 'en' }, 'tool_test')).conversation_config.agent.language, 'en');
});

test('missing credentials or voice is rejected before any remote resources are created', async () => {
  let calls = 0;
  for (const cfg of [{ ...dummyConfig, ELEVENLABS_API_KEY: '' }, { ...dummyConfig, ELEVENLABS_VOICE_ID: '' }]) {
    await assert.rejects(provisionAgent(cfg, { fetchImpl: async () => { calls++; throw new Error('Should not call'); } }), /fehlt/);
  }
  assert.equal(calls, 0);
});

test('partial provision failure retains the created tool ID and does not retry silently', async () => {
  let calls = 0;
  await assert.rejects(provisionAgent(dummyConfig, { fetchImpl: async () => {
    calls++; return calls === 1 ? { ok: true, json: async () => ({ id: 'tool_retained' }) } : { ok: false, status: 403 };
  } }), error => {
    assert.equal(error.toolId, 'tool_retained');
    assert.deepEqual(error.createdResources, { toolId: 'tool_retained' });
    assert.ok(error.message.includes('Dashboard')); assertNoSecrets(error.message); return true;
  });
  assert.equal(calls, 2);
});

test('cancellation between remote creations preserves the successful tool ID', async () => {
  const controller = new AbortController(); let calls = 0;
  await assert.rejects(provisionAgent(dummyConfig, { signal: controller.signal, fetchImpl: async () => {
    calls++;
    return { ok: true, json: async () => { controller.abort(new Error('cancelled')); return { id: 'tool_created_before_cancel' }; } };
  } }), error => {
    assert.equal(error.createdResources.toolId, 'tool_created_before_cancel');
    assert.ok(error.message.includes('cancelled')); return true;
  });
  assert.equal(calls, 1);
});

test('a successful agent response preserves both IDs despite cancellation after creation', async () => {
  const controller = new AbortController(); let calls = 0;
  const result = await provisionAgent(dummyConfig, { signal: controller.signal, fetchImpl: async () => {
    calls++;
    return { ok: true, json: async () => {
      if (calls === 1) return { id: 'tool_created' };
      controller.abort(new Error('late cancellation'));
      return { agent_id: 'agent_created' };
    } };
  } });
  assert.deepEqual(result, { agentId: 'agent_created', toolId: 'tool_created' });
});

test('existing agent diagnostics report configuration problems without private webhook fields', async () => {
  const good = await buildAgentPayload(dummyConfig, 'tool_test');
  const template = JSON.parse(await readFile(new URL('../agent-template.json', import.meta.url), 'utf8'));
  good.privateCredential = dummyConfig.ELEVENLABS_API_KEY;
  const goodView = await readAgentConfiguration(dummyConfig, { fetchImpl: async url => ({ ok: true, json: async () => url.includes('/tools/') ? { tool_config: template.tool.tool_config } : good }) });
  assert.equal(goodView.ready, true); assertNoSecrets(goodView);
  assert.equal(goodView.gameToolId, dummyConfig.ELEVENLABS_GAME_TOOL_ID);
  const badView = await readAgentConfiguration(dummyConfig, { fetchImpl: async () => ({ ok: true, json: async () => ({ name: 'Existing' }) }) });
  assert.equal(badView.ready, false); assert.ok(badView.problems.some(message => message.includes('agent_response_complete')));
  assert.ok(badView.problems.some(message => message.includes('submit_game_turn')));
  assertNoSecrets(badView);
});
