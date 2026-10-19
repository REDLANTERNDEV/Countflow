import {
  LANGUAGE_LABELS,
  SUPPORTED_LANGUAGES,
  currentLanguage,
  setLanguage,
  useTranslation,
  type SupportedLanguage,
} from '@countflow/i18n';
import { Button, Field, Select, useTheme, type ThemePreference } from '@countflow/ui';
import { demo } from '../api.js';

/**
 * Language and theme, on the device itself.
 *
 * The console has had both since Phase 3 and the handheld had neither, which
 * left the two settings that are *most* physical — what language the person
 * holding it reads, and whether a screen is legible under a loading bay's
 * floodlight or in a chilled aisle at 6am — reachable only from a desk the
 * operator does not sit at. Both are stored per device rather than per account
 * (see `ThemeProvider.setPreference`), so a shared handheld on a charging rack
 * keeps the setting the shift picked for it.
 *
 * Rendered on the sign-in screen as well as on Home, and that is the important
 * half: someone who cannot read the language it is currently in has to be able
 * to change it *before* they can sign in, not after.
 */
export function DeviceSettings({ className }: { className?: string }) {
  const { t } = useTranslation();
  const { preference, setPreference } = useTheme();

  return (
    <div className={className}>
      {demo ? <Button size="sm" variant="ghost" onClick={() => void demo?.reset()}>{t('demo.reset')}</Button> : null}
      <div className="grid grid-cols-2 gap-[var(--space-3)]">
        <Field label={t('common.language')} htmlFor="hh-language">
          <Select
            id="hh-language"
            value={currentLanguage()}
            onChange={(event) => setLanguage(event.target.value as SupportedLanguage)}
          >
            {SUPPORTED_LANGUAGES.map((language) => (
              // Each language names itself — "Türkçe", not "Turkish". Somebody
              // who cannot read the current one still has to find their own.
              <option key={language} value={language}>
                {LANGUAGE_LABELS[language]}
              </option>
            ))}
          </Select>
        </Field>

        <Field label={t('common.theme')} htmlFor="hh-theme">
          <Select
            id="hh-theme"
            value={preference}
            onChange={(event) => setPreference(event.target.value as ThemePreference)}
          >
            {/* System first and default — a device nobody has touched follows
                the OS, including when it flips at dusk. */}
            <option value="system">{t('common.themeSystem')}</option>
            <option value="light">{t('common.themeLight')}</option>
            <option value="dark">{t('common.themeDark')}</option>
          </Select>
        </Field>
      </div>
    </div>
  );
}
