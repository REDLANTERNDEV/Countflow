import { Select, useTheme, type ThemePreference } from '@countflow/ui';
import { useTranslation } from '@countflow/i18n';

export function ThemeSwitcher() {
  const { preference, setPreference } = useTheme();
  const { t } = useTranslation();

  return (
    <Select
      aria-label={t('common.theme')}
      value={preference}
      onChange={(event) => setPreference(event.target.value as ThemePreference)}
      className="h-[var(--control-height-sm)] text-[length:var(--text-xs)]"
    >
      {/* System first and default — someone who never touches this follows
          their OS, including when it flips at dusk. */}
      <option value="system">{t('common.themeSystem')}</option>
      <option value="light">{t('common.themeLight')}</option>
      <option value="dark">{t('common.themeDark')}</option>
    </Select>
  );
}
