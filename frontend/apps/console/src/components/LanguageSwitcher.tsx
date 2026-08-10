import { Select } from '@countflow/ui';
import {
  LANGUAGE_LABELS,
  SUPPORTED_LANGUAGES,
  currentLanguage,
  setLanguage,
  useTranslation,
  type SupportedLanguage,
} from '@countflow/i18n';

export function LanguageSwitcher() {
  const { t } = useTranslation();

  return (
    <Select
      aria-label={t('common.language')}
      value={currentLanguage()}
      onChange={(event) => setLanguage(event.target.value as SupportedLanguage)}
      className="h-[var(--control-height-sm)] text-[length:var(--text-xs)]"
    >
      {SUPPORTED_LANGUAGES.map((language) => (
        // Each language is named in itself — "Türkçe", not "Turkish". Someone
        // who cannot read the current language still needs to find their own.
        <option key={language} value={language}>
          {LANGUAGE_LABELS[language]}
        </option>
      ))}
    </Select>
  );
}
