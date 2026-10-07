const form = document.querySelector('#config'); const $ = selector => document.querySelector(selector);
async function api(url, body) {
  const response = await fetch(url, body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const data = await response.json(); if (!response.ok) throw new Error(data.error?.message ?? 'Anfrage fehlgeschlagen.'); return data;
}
function status(data) { $('#status').textContent = data.configured ? 'ElevenLabs-Zugang und Agent gespeichert · Verbindung über „Agent prüfen“ testen' : 'Einrichtung offen · API-Schlüssel speichern und einen Spiel-Agenten verbinden oder anlegen'; }
function fill(data) {
  status(data); for (const [key, field] of [['agentId', 'ELEVENLABS_AGENT_ID'], ['gameToolId', 'ELEVENLABS_GAME_TOOL_ID'], ['voiceId', 'ELEVENLABS_VOICE_ID'], ['language', 'ELEVENLABS_LANGUAGE'], ['languageMode', 'ELEVENLABS_LANGUAGE_MODE'], ['ttsModel', 'ELEVENLABS_TTS_MODEL'], ['callerFallbackLanguage', 'ELEVENLABS_CALLER_FALLBACK_LANGUAGE'], ['timeoutSeconds', 'REQUEST_TIMEOUT_SECONDS']]) if (data[key] !== undefined) form.elements[field].value = data[key];
}
async function refresh() { try { fill(await api('/api/status')); } catch (error) { $('#status').textContent = error.message; } }
form.addEventListener('submit', async event => {
  event.preventDefault(); const button = form.querySelector('button[type=submit]'); button.disabled = true;
  try {
    const values = Object.fromEntries(new FormData(form)); values.CLEAR_ELEVENLABS_API_KEY = form.elements.CLEAR_ELEVENLABS_API_KEY.checked; values.REQUEST_TIMEOUT_SECONDS = Number(values.REQUEST_TIMEOUT_SECONDS);
    fill(await api('/api/config', values)); form.elements.ELEVENLABS_API_KEY.value = ''; form.elements.CLEAR_ELEVENLABS_API_KEY.checked = false;
    $('#save-result').textContent = 'Gespeichert. Die nächsten Anfragen verwenden diese Einstellungen.';
  } catch (error) { $('#save-result').textContent = error.message; } finally { button.disabled = false; }
});
$('#create').addEventListener('click', async () => {
  $('#create').disabled = true; $('#agent-result').textContent = 'Agent und Spielstatus-Tool werden angelegt …';
  try { fill(await api('/api/create-agent', {})); $('#agent-result').textContent = 'Agent und Spielstatus-Tool angelegt. Zugangsdaten lokal gespeichert. Jetzt Gespräch testen.'; }
  catch (error) { $('#agent-result').textContent = error.message; } finally { $('#create').disabled = false; }
});
$('#check').addEventListener('click', async () => {
  $('#check').disabled = true;
  try { const data = await api('/api/check-agent', {}); $('#agent-result').textContent = data.ready ? `Agent ${data.name} ist passend konfiguriert. Modell: ${data.model}.` : data.problems.join(' '); if (data.ready) await refresh(); }
  catch (error) { $('#agent-result').textContent = error.message; } finally { $('#check').disabled = false; }
});
let audioUrl;
async function playVoice(body, route = '/v1/audio/speech') {
  const response = await fetch(route, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ...body, response_format: 'wav' }) });
  if (!response.ok) { const error = await response.json(); throw new Error(error.error?.message ?? 'Agent-Sprachausgabe fehlgeschlagen.'); }
  if (audioUrl) URL.revokeObjectURL(audioUrl); audioUrl = URL.createObjectURL(await response.blob()); $('#audio').src = audioUrl; $('#audio').style.display = 'block'; await $('#audio').play();
}
$('#enable-voices').addEventListener('click', async () => {
  $('#enable-voices').disabled = true;
  try { await api('/api/enable-character-voices', {}); $('#agent-result').textContent = 'Stimme und Sprachmodell können jetzt pro Anrufer gewählt werden.'; }
  catch (error) { $('#agent-result').textContent = error.message; } finally { $('#enable-voices').disabled = false; }
});
async function loadCallers() {
  $('#load-callers').disabled = true;
  try {
    const [{ callers }, { voices }] = await Promise.all([api('/api/callers'), api('/api/voices')]);
    const cards = callers.map(caller => {
      const card = document.createElement('div'); card.className = 'caller-card';
      const title = document.createElement('h3'); title.textContent = caller.name; card.append(title);
      const detail = document.createElement('p'); detail.className = 'muted'; detail.textContent = `Charakter-Sprache: ${caller.nativeLanguage || 'nicht hinterlegt'} · ${caller.gender || 'Geschlecht unbekannt'}${caller.age ? ` · ${caller.age} Jahre` : ''}`; card.append(detail);
      const languageLabel = document.createElement('label'); languageLabel.textContent = 'Sprache'; const language = document.createElement('input'); language.value = caller.language; language.maxLength = 10; languageLabel.append(language); card.append(languageLabel);
      const voiceLabel = document.createElement('label'); voiceLabel.textContent = 'Stimme'; const voice = document.createElement('select');
      for (const item of voices) { const option = document.createElement('option'); option.value = item.id; option.textContent = `${item.name} · ${item.language || 'mehrsprachig'} · ${item.gender || 'unbekannt'} · ${item.age || 'Alter unbekannt'}`; voice.append(option); }
      voice.value = caller.voiceId; voiceLabel.append(voice); card.append(voiceLabel);
      if (caller.selectionWarning) { const warning = document.createElement('p'); warning.textContent = caller.selectionWarning; card.append(warning); }
      const row = document.createElement('div'); row.className = 'row';
      const save = document.createElement('button'); save.textContent = 'Für diesen Anrufer speichern'; save.addEventListener('click', async () => {
        save.disabled = true; try { await api('/api/callers', { caller_id: caller.id, language_code: language.value, voice_id: voice.value }); $('#caller-result').textContent = `${caller.name}: gespeichert.`; } catch (error) { $('#caller-result').textContent = error.message; } finally { save.disabled = false; }
      }); row.append(save);
      const reset = document.createElement('button'); reset.textContent = 'Automatische Auswahl'; reset.addEventListener('click', async () => { try { await api('/api/callers', { caller_id: caller.id, reset: true }); await loadCallers(); } catch (error) { $('#caller-result').textContent = error.message; } }); row.append(reset);
      const preview = document.createElement('button'); preview.textContent = 'Stimme anhören'; preview.addEventListener('click', async () => {
        preview.disabled = true;
        try {
          await playVoice({ caller_id: caller.id, voice_id: voice.value, language_code: language.value }, '/api/preview-voice');
        } catch (error) { $('#caller-result').textContent = error.message; } finally { preview.disabled = false; }
      }); row.append(preview); card.append(row); return card;
    });
    $('#callers').replaceChildren(...cards); $('#caller-result').textContent = callers.length ? `${callers.length} Anrufer · ${voices.length} verfügbare Stimmen` : 'Noch keine Anrufer erkannt. Mod 1.0.1 laden und einen neuen Anruf beginnen.';
  } catch (error) { $('#caller-result').textContent = error.message; } finally { $('#load-callers').disabled = false; }
}
$('#load-callers').addEventListener('click', loadCallers);
$('#test').addEventListener('click', async () => {
  $('#test').disabled = true; $('#test-result').textContent = 'ElevenLabs Agent antwortet …'; $('#metrics').textContent = '';
  try {
    const data = await api('/api/test', {}), answer = JSON.parse(data.content); $('#test-result').textContent = answer.dialogue;
    $('#metrics').textContent = `Erster Text: ${data.metrics.firstTextMs ?? '–'} ms · Antwort mit Spielstatus: ${data.metrics.completionMs} ms · erstes Audio: ${data.metrics.firstAudioMs ?? 'noch unterwegs'} ms`;
    await playVoice({ input: answer.dialogue });
  } catch (error) { $('#test-result').textContent += ` ${error.message}`; } finally { $('#test').disabled = false; }
});
refresh();
