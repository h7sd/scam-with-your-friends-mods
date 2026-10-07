import test from 'node:test';
import assert from 'node:assert/strict';
import { buildPrompt, validateRequest, normalizeContent, partialJsonString } from '../src/completion.js';
import { callerRequest } from './helpers.js';

test('normalizer enforces exact game types, ranges, enums and additionalProperties', () => {
  const request = callerRequest();
  const valid = { dialogue: 'Hallo!', trust_percent: 42, emotion: 'NEUTRAL' };
  assert.deepEqual(JSON.parse(normalizeContent('```json\n' + JSON.stringify(valid) + '\n```', request)), valid);
  for (const value of [
    { ...valid, trust_percent: '42' }, { ...valid, trust_percent: 101 },
    { ...valid, trust_percent: 0.5 }, { ...valid, emotion: 'OTHER' },
    { ...valid, dialogue: 12 }, { ...valid, unexpected: true },
    { dialogue: 'Hallo!', emotion: 'NEUTRAL' }, [], null,
  ]) assert.throws(() => normalizeContent(JSON.stringify(value), request), /Spielschema/);
  assert.throws(() => normalizeContent('not JSON', request), /gültiges JSON/);
  assert.throws(() => normalizeContent(' ', request), /leere Antwort/);
});

test('alternate json_schema and plain response formats are supported', () => {
  const request = callerRequest();
  request.response_format = { type: 'json_schema', schema: request.response_format.json_schema.schema };
  const content = '{ "dialogue":"Hi", "trust_percent":0, "emotion":"ANGRY" }';
  assert.equal(normalizeContent(content, request), '{"dialogue":"Hi","trust_percent":0,"emotion":"ANGRY"}');
  assert.equal(normalizeContent('  prose  ', { messages: [] }), 'prose');
  assert.equal(normalizeContent('```json\n{"a":1}\n```', { response_format: { type: 'json_object' } }), '{"a":1}');
});

test('request validation rejects malformed chats without invoking the backend', () => {
  for (const value of [null, {}, { messages: [] }, { messages: [{ role: 'tool', content: 'x' }] },
    { messages: [{ role: 'user', content: [{ type: 'text', text: 'x' }] }] },
    { messages: Array.from({ length: 201 }, () => ({ role: 'user', content: 'x' })) },
  ]) assert.throws(() => validateRequest(value));
  assert.doesNotThrow(() => validateRequest({ messages: ['system', 'developer', 'user', 'assistant'].map(role => ({ role, content: 'x' })) }));
  const prompt = buildPrompt(callerRequest());
  assert.ok(prompt.includes('"additionalProperties":false'));
  assert.ok(prompt.includes('CHAT CONVERSATION:'));
});

test('partial JSON dialogue is decoded incrementally across every token boundary', () => {
  const value = 'Er sagt: "Hallo". Pfad C:\\Spiel / Zeile\nTab\tGrüße 😀';
  const source = JSON.stringify({ metadata: { dialogue: 'do not speak' }, dialogue: value, emotion: 'NEUTRAL' });
  let prior = '';
  for (let i = 0; i <= source.length; i++) {
    const result = partialJsonString(source.slice(0, i), 'dialogue');
    if (!result) continue;
    assert.ok(value.startsWith(result.value), `decoded invalid prefix at offset ${i}`);
    assert.ok(result.value.startsWith(prior), `prefix regressed at offset ${i}`);
    assert.ok(!/[\uD800-\uDBFF]$/.test(result.value), 'no half surrogate is emitted');
    prior = result.value;
  }
  assert.deepEqual(partialJsonString(source, 'dialogue'), { value, complete: true });
});

test('escaped unicode, quotes, backslashes and split escapes retain only stable text', () => {
  const prefix = '{"dialogue":"Hi ';
  const source = prefix + '\\uD83D\\uDE00 \\"quote\\" \\\\ \\/ \\b \\f \\r \\t\\n"}';
  const final = JSON.parse(source).dialogue;
  for (let end = prefix.length; end <= source.length; end++) {
    const result = partialJsonString(source.slice(0, end), 'dialogue');
    assert.ok(result && final.startsWith(result.value));
    assert.ok(!/[\uD800-\uDBFF]$/.test(result.value));
  }
  assert.deepEqual(partialJsonString(source, 'dialogue'), { value: final, complete: true });
  assert.equal(partialJsonString('{"dialogue":"bad\\q', 'dialogue'), null);
  assert.equal(partialJsonString('{"dialogue":"bad\\uGGGG', 'dialogue'), null);
  assert.equal(partialJsonString('{"nested":{"dialogue":"hidden"}}', 'dialogue'), null);
  assert.equal(partialJsonString('{"dialogue":5}', 'dialogue'), null);
});
