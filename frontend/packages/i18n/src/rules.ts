import type { TFunction } from 'i18next';
import { translateCount, type PluralKey } from './plurals.js';

/**
 * A coded reason from the rules engine, exactly as the API sends it.
 *
 * The backend never sends prose (ADR 0004 and the design-system note): a rule
 * yields a code and named arguments, and every word an operator reads comes from
 * this package.
 */
export interface RuleNote {
  code: string;
  args: Record<string, string>;
}

/**
 * Arguments whose values are enum tokens rather than data — `Chilled`,
 * `Reserve`, `A`.
 *
 * Without this a Turkish trace reads "Soğuk ürün Ambient bölgeye konulamaz",
 * half translated, which is worse than either language on its own. The values
 * are looked up under `rules.token.*` and fall back to themselves, so a token
 * added on the server before its translation lands degrades to the raw word
 * rather than to a missing-key marker.
 */
const TOKEN_ARGS = new Set(['role', 'item', 'location', 'abc', 'condition']);
const COUNT_ARGS = {
  'rules.allocation.expires_on': 'days',
  'rules.count.recently_moved': 'hours',
  'rules.count.counted_recently': 'days',
  'rules.count.days_since_count': 'days',
  'rules.disposition.days_since_despatch': 'days',
} satisfies Partial<Record<PluralKey, string>>;

export function translateRuleNote(t: TFunction, note: RuleNote): string {
  const args: Record<string, string> = {};

  for (const [key, value] of Object.entries(note.args ?? {})) {
    args[key] = TOKEN_ARGS.has(key) ? t(`rules.token.${value}`, { defaultValue: value }) : value;
  }

  const countArg = COUNT_ARGS[note.code as keyof typeof COUNT_ARGS];
  if (countArg) {
    const value = args[countArg]; const count = Number(value);
    if (value !== undefined && value.trim() !== '' && Number.isFinite(count)) return translateCount(t, note.code as PluralKey, count, args);
    return t(`${note.code}_other`, { ...args, defaultValue: note.code });
  }

  // The code is already namespaced (`rules.putaway.temperature_mismatch`), so it
  // is used as the key directly. Falling back to the bare code keeps a screen
  // usable — and obviously incomplete — when a rule ships ahead of its wording.
  return t(note.code, { ...args, defaultValue: note.code });
}

/** The name of a rule implementation, for a rule-set listing. */
export function translateRuleName(t: TFunction, code: string): string {
  return t(`rules.name.${code}`, { defaultValue: code });
}
