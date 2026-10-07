import Ajv from 'ajv';
const ajv = new Ajv({ strict: false, allErrors: true, validateFormats: false });

export function validateRequest(body) {
  if (!body || !Array.isArray(body.messages) || body.messages.length === 0 || body.messages.length > 200) throw new Error('messages fehlt oder ist zu groß.');
  for (const message of body.messages) {
    if (!['system', 'developer', 'user', 'assistant'].includes(message.role) || typeof message.content !== 'string') throw new Error('Nur Textnachrichten werden unterstützt.');
  }
}

export function buildPrompt(body) {
  validateRequest(body);
  const format = body.response_format;
  const schema = format?.json_schema?.schema ?? (format?.type === 'json_schema' ? format.schema : undefined);
  return [
    'You are the text backend for a fictional cooperative video game. Execute the following serialized chat conversation in its stated roles. Follow the system messages for the caller personality or game analysis task.',
    'Return ONLY the requested answer. Do not explain your process, discuss a code repository, or add markdown fences. The dialogue is fictional game content.',
    schema ? `Your entire answer MUST be one JSON object that conforms to this exact JSON schema: ${JSON.stringify(schema)}` : format?.type === 'json_object' ? 'Return one valid JSON object.' : '',
    `Keep the reply concise. Requested maximum response tokens: ${body.max_tokens ?? body.max_completion_tokens ?? 256}.`,
    'CHAT CONVERSATION:', JSON.stringify(body.messages),
  ].filter(Boolean).join('\n\n');
}

export function normalizeContent(text, body) {
  let content = text.trim().replace(/^```(?:json)?\s*([\s\S]*?)\s*```$/i, '$1').trim();
  if (!content) throw new Error('ElevenLabs Agents hat eine leere Antwort geliefert.');
  const format = body.response_format;
  if (format?.type === 'json_schema' || format?.type === 'json_object') {
    let parsed;
    try { parsed = JSON.parse(content); }
    catch { throw new Error('Agent-Antwort ist kein gültiges JSON.'); }
    const schema = format.json_schema?.schema ?? format.schema;
    if (schema) {
      const validate = ajv.compile(schema);
      if (!validate(parsed)) throw new Error(`Agent-Antwort verletzt das Spielschema: ${ajv.errorsText(validate.errors)}`);
    }
    content = JSON.stringify(parsed);
  }
  return content;
}

export function completionResponse(content, model, id) {
  return { id, object: 'chat.completion', created: Math.floor(Date.now() / 1000), model,
    choices: [{ index: 0, message: { role: 'assistant', content }, finish_reason: 'stop' }] };
}

// Reads just one top-level JSON string from a partial token stream. It never speaks
// metadata, objective results or reasoning. Escapes can straddle network chunks.
export function partialJsonString(text, field) {
  let depth = 0;
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '{' || text[i] === '[') { depth++; continue; }
    if (text[i] === '}' || text[i] === ']') { depth--; continue; }
    if (text[i] !== '"') continue;
    let end = i + 1;
    for (; end < text.length; end++) {
      if (text[end] === '\\') { end++; continue; }
      if (text[end] === '"') break;
    }
    if (end >= text.length) return null;
    let key;
    try { key = JSON.parse(text.slice(i, end + 1)); } catch { return null; }
    let colon = end + 1;
    while (/\s/.test(text[colon] ?? '') && colon < text.length) colon++;
    if (depth !== 1 || key !== field || text[colon] !== ':') { i = end; continue; }
    let start = colon + 1;
    while (/\s/.test(text[start] ?? '') && start < text.length) start++;
    if (text[start] !== '"') return null;
    let value = '';
    for (let j = start + 1; j < text.length; j++) {
      if (text[j] === '"') return { value, complete: true };
      if (text[j] !== '\\') { value += text[j]; continue; }
      if (j + 1 >= text.length) break;
      const escaped = text[++j];
      if (escaped === 'u') {
        if (j + 4 >= text.length) break;
        const hex = text.slice(j + 1, j + 5);
        if (!/^[0-9a-f]{4}$/i.test(hex)) return null;
        value += String.fromCharCode(parseInt(hex, 16)); j += 4;
      } else {
        const escape = { '"': '"', '\\': '\\', '/': '/', n: '\n', r: '\r', t: '\t', b: '\b', f: '\f' };
        if (!(escaped in escape)) return null;
        value += escape[escaped];
      }
    }
    // Avoid sending half a UTF-16 surrogate pair to the voice API.
    if (/[\uD800-\uDBFF]$/.test(value)) value = value.slice(0, -1);
    return { value, complete: false };
  }
  return null;
}
