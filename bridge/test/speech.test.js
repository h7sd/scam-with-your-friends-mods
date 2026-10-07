import test from 'node:test';
import assert from 'node:assert/strict';
import { AudioBuffer, SpeechCache, wav, sampleRate } from '../src/speech.js';
import { dummyConfig, deferred, within } from './helpers.js';

test('audio subscribers receive existing and live PCM before completion', async () => {
  const audio = new AudioBuffer(), first = Buffer.from([1, 0]), second = Buffer.from([2, 0]);
  audio.push(first);
  const chunks = [], stop = audio.subscribe(chunk => chunks.push(chunk));
  let completed = false;
  audio.done.then(() => { completed = true; });
  audio.push(second);
  assert.deepEqual(chunks, [first, second]);
  assert.equal(completed, false);
  stop(); audio.finish();
  assert.deepEqual(await audio.done, Buffer.concat([first, second]));
  assert.equal(completed, true);
  assert.equal(audio.finished, true);
});

test('PCM conversion emits valid mono s16le RIFF header and rejects corrupt data', async () => {
  const pcm = Buffer.from([1, 0, 2, 0]), result = wav(pcm);
  assert.equal(result.toString('ascii', 0, 4), 'RIFF');
  assert.equal(result.readUInt32LE(4), result.length - 8);
  assert.equal(result.toString('ascii', 8, 12), 'WAVE');
  assert.equal(result.readUInt16LE(20), 1);
  assert.equal(result.readUInt16LE(22), 1);
  assert.equal(result.readUInt32LE(24), sampleRate);
  assert.equal(result.readUInt32LE(28), sampleRate * 2);
  assert.equal(result.readUInt16LE(34), 16);
  assert.equal(result.readUInt32LE(40), pcm.length);
  assert.deepEqual(result.subarray(44), pcm);
  assert.throws(() => wav(Buffer.from([1])), /ungerade/);
  for (const pcm of [Buffer.alloc(0), Buffer.from([1])]) {
    const audio = new AudioBuffer(); audio.push(pcm); audio.finish();
    await assert.rejects(audio.done, /gültiges PCM/);
  }
});

test('a full speech cache reuses prefetched audio without a second Agents call', async () => {
  let generations = 0;
  const cache = new SpeechCache({ maxEntries: 1, createSpeech: async () => { generations++; return new AudioBuffer(); } });
  const audio = new AudioBuffer();
  cache.put(' Hallo! ', dummyConfig, audio);
  assert.equal(await cache.take('Hallo!', dummyConfig), audio);
  assert.equal(generations, 0);
  assert.equal(cache.entries.size, 0);
  audio.push(Buffer.from([1, 0])); audio.finish();
  assert.ok((await audio.done).length);
  const next = await cache.take('Hallo!', dummyConfig);
  assert.equal(generations, 1);
  next.fail(new Error('test complete')); cache.close();
});

test('cache separates agents and keys, expires and evicts live sessions', async () => {
  const cache = new SpeechCache({ maxEntries: 1, createSpeech: async () => new AudioBuffer() });
  assert.notEqual(cache.key('x', dummyConfig), cache.key('x', { ...dummyConfig, ELEVENLABS_AGENT_ID: 'agent_other' }));
  assert.notEqual(cache.key('x', dummyConfig), cache.key('x', { ...dummyConfig, ELEVENLABS_API_KEY: 'other_key' }));
  const old = new AudioBuffer(), current = new AudioBuffer();
  cache.put('old', dummyConfig, old); cache.put('current', dummyConfig, current);
  await assert.rejects(old.done, /ersetzt/);
  cache.entries.get(cache.key('current', dummyConfig)).expires = Date.now() - 1;
  cache.prune();
  await assert.rejects(current.done, /abgelaufen/);
  assert.equal(cache.entries.size, 0);
  cache.close();
});

test('aborting cached WAV output cancels the underlying audio producer', async () => {
  const cache = new SpeechCache(), audio = new AudioBuffer(), controller = new AbortController();
  const finished = deferred(); audio.onFinish = finished.resolve;
  cache.put('x', dummyConfig, audio);
  const pending = cache.audio('x', dummyConfig, controller.signal);
  await Promise.resolve(); controller.abort();
  await assert.rejects(within(pending), /abgebrochen/);
  await within(finished.promise);
  assert.equal(audio.finished, true); cache.close();
});
