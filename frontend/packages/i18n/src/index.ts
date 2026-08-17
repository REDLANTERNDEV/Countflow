import i18n from 'i18next';
import LanguageDetector from 'i18next-browser-languagedetector';
import { initReactI18next } from 'react-i18next';
import { en } from './locales/en.js';
import { tr } from './locales/tr.js';

export const SUPPORTED_LANGUAGES = ['en', 'tr'] as const;
export type SupportedLanguage = (typeof SUPPORTED_LANGUAGES)[number];

export const LANGUAGE_STORAGE_KEY = 'countflow.language';

export const LANGUAGE_LABELS: Record<SupportedLanguage, string> = {
  en: 'English',
  tr: 'Türkçe',
};

/**
 * Initialises i18next for a Countflow app.
 *
 * Detection order puts an explicit choice ahead of the browser's list, so a user
 * who picked a language keeps it.
 *
 * `namespaces` narrows what i18next loads into memory. It is a runtime filter,
 * not a bundling one — the catalogues are statically imported, so every string
 * is still in the build regardless.
 */
/**
 * Drops a stored language that nobody chose.
 *
 * Until the detector stopped caching, every first launch wrote what it had
 * detected — as the raw navigator tag, so `en-US` rather than `en`. `setLanguage`
 * has only ever written an exact supported code, which makes anything else in
 * that slot provably a detection artefact rather than a decision, and safe to
 * clear. Without this the fix reaches only installs that have never been opened.
 */
function forgetDetectedLanguage(): void {
  try {
    const stored = window.localStorage.getItem(LANGUAGE_STORAGE_KEY);
    if (stored !== null && !(SUPPORTED_LANGUAGES as readonly string[]).includes(stored)) {
      window.localStorage.removeItem(LANGUAGE_STORAGE_KEY);
    }
  } catch {
    // A device that will not give us storage has no stale value to forget.
  }
}

export function initI18n(namespaces?: string[]) {
  forgetDetectedLanguage();

  const pick = <T,>(catalogue: T): Partial<T> => {
    if (!namespaces) return catalogue as Partial<T>;
    return Object.fromEntries(
      Object.entries(catalogue as Record<string, unknown>).filter(([key]) =>
        namespaces.includes(key),
      ),
    ) as Partial<T>;
  };

  void i18n
    .use(LanguageDetector)
    .use(initReactI18next)
    .init({
      resources: {
        en: { translation: pick(en) },
        tr: { translation: pick(tr) },
      },
      fallbackLng: 'en',
      supportedLngs: [...SUPPORTED_LANGUAGES],
      // Treat "tr-TR" as "tr" — we ship languages, not regional variants.
      load: 'languageOnly',
      nonExplicitSupportedLngs: true,
      detection: {
        order: ['localStorage', 'navigator'],
        lookupLocalStorage: LANGUAGE_STORAGE_KEY,
        // Nothing is written back on *detection*. The detector's `caches` option
        // persists whatever it worked out, which turns "we guessed English from
        // your browser" into a stored value indistinguishable from "this person
        // chose English" — and from then on the app ignores the browser it was
        // meant to follow. A device set to Turkish after first launch went on
        // showing English forever, and what it had actually stored was the raw
        // navigator tag `en-US`. Only `setLanguage` writes now, so an untouched
        // install follows the browser every time it starts.
        caches: [],
      },
      interpolation: {
        // React already escapes. Doing it twice turns an apostrophe in a
        // description into `&#39;` on screen.
        escapeValue: false,
      },
    });

  // `<html lang>` is hardcoded to "en" in both index.html files, and it is what
  // a screen reader picks its voice from. Kept in step with the language that
  // actually resolved, and with any later change.
  const syncDocumentLanguage = () => {
    document.documentElement.lang = currentLanguage();
  };

  syncDocumentLanguage();
  i18n.on('languageChanged', syncDocumentLanguage);

  return i18n;
}

/**
 * Records a deliberate choice, and is the only thing that writes one.
 *
 * Persisted per device rather than per account, for the reason the theme is
 * (`ThemeProvider.setPreference`): a shared handheld off a charging rack should
 * keep what the shift set on it.
 */
export function setLanguage(language: SupportedLanguage): void {
  window.localStorage.setItem(LANGUAGE_STORAGE_KEY, language);
  void i18n.changeLanguage(language);
}

export function currentLanguage(): SupportedLanguage {
  const resolved = i18n.resolvedLanguage ?? i18n.language ?? 'en';
  return (SUPPORTED_LANGUAGES as readonly string[]).includes(resolved)
    ? (resolved as SupportedLanguage)
    : 'en';
}

/**
 * BCP-47 tag for `Intl`. i18next's language and the formatting locale are not
 * the same thing — `Intl` wants a region to pick the right separators, and
 * plain `tr` would not give Turkish number grouping reliably.
 */
export function currentLocale(): string {
  return currentLanguage() === 'tr' ? 'tr-TR' : 'en-GB';
}

export { en, tr };
export type { Catalogue } from './locales/en.js';
export {
  formatDate,
  formatDateTime,
  formatIdentifier,
  formatNumber,
  upperCaseIdentifier,
} from './format.js';
export { translateRuleNote, translateRuleName } from './rules.js';
export type { RuleNote } from './rules.js';
export { useTranslation, Trans } from 'react-i18next';
