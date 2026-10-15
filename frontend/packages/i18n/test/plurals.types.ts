import type { TFunction } from 'i18next';
import { translateCount } from '../src/plurals.js';

export function translationTypeChecks(t: TFunction) {
  translateCount(t, 'queue.needsYou', 1);
  translateCount(t, 'accuracy.stranded.age', 1, { days: 1 });
  // @ts-expect-error A formatted string is not a cardinal count.
  translateCount(t, 'queue.needsYou', '1');
  // @ts-expect-error Misspelled catalogue keys cannot silently fall back.
  translateCount(t, 'queue.needYou', 1);
  // @ts-expect-error A neutral label is not a plural family.
  translateCount(t, 'common.total', 1);
}
