import { ref } from 'vue'
import { en as vEn, fr as vFr, es as vEs, it as vIt, de as vDe } from 'vuetify/locale'
import fr from './locales/fr'
import en from './locales/en'
import es from './locales/es'
import it from './locales/it'
import de from './locales/de'

// French is the reference language (the source of the texts); the others fall back on it.
export const languages = [
  { code: 'fr', name: 'Français', flag: '🇫🇷' },
  { code: 'en', name: 'English', flag: '🇬🇧' },
  { code: 'es', name: 'Español', flag: '🇪🇸' },
  { code: 'it', name: 'Italiano', flag: '🇮🇹' },
  { code: 'de', name: 'Deutsch', flag: '🇩🇪' },
]

const messages = { fr, en, es, it, de }
export const vuetifyMessages = { fr: vFr, en: vEn, es: vEs, it: vIt, de: vDe }

const STORAGE_KEY = 'ipx2mqtt.lang'

function initialLocale() {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved && messages[saved]) return saved
  } catch { /* storage unavailable */ }
  const browser = (navigator.language || 'fr').slice(0, 2).toLowerCase()
  return messages[browser] ? browser : 'fr'
}

export const locale = ref(initialLocale())

export function setLocale(code) {
  if (!messages[code]) return
  locale.value = code
  document.documentElement.lang = code
  try {
    localStorage.setItem(STORAGE_KEY, code)
  } catch { /* storage unavailable */ }
}
document.documentElement.lang = locale.value

/** Translates a key; returns null when the key does not exist in any language (so callers can skip optional texts). */
export function tOrNull(key, params = {}) {
  const text = messages[locale.value]?.[key] ?? messages.en[key] ?? messages.fr[key]
  if (text === undefined) return null
  return text.replace(/\{(\w+)\}/g, (_, name) => (params[name] !== undefined ? params[name] : `{${name}}`))
}

export const t = (key, params) => tOrNull(key, params) ?? key
