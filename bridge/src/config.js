import { readFile, writeFile, rename, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const bridgeRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const configPath = path.join(bridgeRoot, '.env');
const defaults = {
  ELEVENLABS_API_KEY: '', ELEVENLABS_AGENT_ID: '', ELEVENLABS_GAME_TOOL_ID: '', ELEVENLABS_VOICE_ID: '', ELEVENLABS_LANGUAGE: 'de',
  ELEVENLABS_LANGUAGE_MODE: 'character', ELEVENLABS_CALLER_FALLBACK_LANGUAGE: 'en', ELEVENLABS_TTS_MODEL: 'eleven_v4_turbo',
  REQUEST_TIMEOUT_SECONDS: 45, PORT: 8765,
};

export function parseEnv(text) {
  const values = {};
  for (const line of text.split(/\r?\n/)) {
    const match = line.match(/^([A-Z_]+)=(.*)$/);
    if (!match || !(match[1] in defaults)) continue;
    try { values[match[1]] = JSON.parse(match[2]); }
    catch { values[match[1]] = match[2].trim(); }
  }
  return values;
}

export function normalizeConfig(values) {
  const cfg = { ...defaults };
  for (const key of Object.keys(defaults)) {
    if (values[key] === undefined) continue;
    if (typeof defaults[key] === 'string') {
      if (typeof values[key] !== 'string' || /[\r\n\0]/.test(values[key])) throw new Error(`Ungültiger Wert: ${key}`);
      cfg[key] = values[key].trim();
    } else if (typeof defaults[key] === 'boolean') {
      cfg[key] = values[key] === true || values[key] === 'true';
    } else {
      cfg[key] = Number(values[key]);
    }
  }
  if (!Number.isInteger(cfg.PORT) || cfg.PORT < 1024 || cfg.PORT > 65535) throw new Error('Port muss 1024–65535 sein.');
  if (!Number.isInteger(cfg.REQUEST_TIMEOUT_SECONDS) || cfg.REQUEST_TIMEOUT_SECONDS < 5 || cfg.REQUEST_TIMEOUT_SECONDS > 300) throw new Error('Timeout muss 5–300 Sekunden sein.');
  if (!/^[a-z]{2,3}(-[A-Z]{2})?$/.test(cfg.ELEVENLABS_LANGUAGE)) throw new Error('Ungültiger Sprachcode.');
  if (!['character','player'].includes(cfg.ELEVENLABS_LANGUAGE_MODE)) throw new Error('Ungültiger Anrufer-Sprachmodus.');
  if (!/^[a-z]{2,3}(-[A-Z]{2})?$/.test(cfg.ELEVENLABS_CALLER_FALLBACK_LANGUAGE)) throw new Error('Ungültige Ersatzsprache für Anrufer.');
  if (!['eleven_v4_turbo','eleven_v4','eleven_v3_conversational','eleven_multilingual_v2','eleven_flash_v2_5','eleven_flash_v2'].includes(cfg.ELEVENLABS_TTS_MODEL)) throw new Error('Ungültiges Sprachmodell.');
  return cfg;
}

export async function loadConfig() {
  let file = {};
  try { file = parseEnv(await readFile(configPath, 'utf8')); }
  catch (err) { if (err.code !== 'ENOENT') throw err; }
  const env = Object.fromEntries(Object.keys(defaults).filter(k => process.env[k] !== undefined).map(k => [k, process.env[k]]));
  return normalizeConfig({ ...file, ...env });
}

export async function saveConfig(config, destination = configPath) {
  const text = Object.keys(defaults).map(key => `${key}=${JSON.stringify(config[key])}`).join('\n') + '\n';
  await mkdir(path.dirname(destination), { recursive: true });
  await writeFile(destination + '.tmp', text, { mode: 0o600 });
  await rename(destination + '.tmp', destination);
}

export function publicConfig(cfg) {
  return {
    service: 'scamwyf-elevenlabs-agents-bridge',
    configured: !!(cfg.ELEVENLABS_API_KEY && cfg.ELEVENLABS_AGENT_ID && cfg.ELEVENLABS_GAME_TOOL_ID),
    voiceConfigured: !!(cfg.ELEVENLABS_API_KEY && cfg.ELEVENLABS_AGENT_ID),
    agentId: cfg.ELEVENLABS_AGENT_ID, gameToolId: cfg.ELEVENLABS_GAME_TOOL_ID, voiceId: cfg.ELEVENLABS_VOICE_ID, language: cfg.ELEVENLABS_LANGUAGE,
    timeoutSeconds: cfg.REQUEST_TIMEOUT_SECONDS,
    languageMode: cfg.ELEVENLABS_LANGUAGE_MODE, callerFallbackLanguage: cfg.ELEVENLABS_CALLER_FALLBACK_LANGUAGE, ttsModel: cfg.ELEVENLABS_TTS_MODEL,
  };
}
