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
export function initI18n(namespaces?: string[]) {
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
        caches: ['localStorage'],
      },
      interpolation: {
        // React already escapes. Doing it twice turns an apostrophe in a
        // description into `&#39;` on screen.
        escapeValue: false,
      },
    });

  return i18n;
}

export function setLanguage(language: SupportedLanguage): void {
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
