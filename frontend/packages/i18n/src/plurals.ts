import type { TFunction } from 'i18next';
import type { Catalogue } from './locales/en.js';

type PluralPaths<T, Prefix extends string = ''> = {
  [Key in keyof T & string]: T[Key] extends string
    ? Key extends `${infer Base}_other` ? `${Prefix}${Base}` : never
    : PluralPaths<T[Key], `${Prefix}${Key}.`>;
}[keyof T & string];
export type PluralKey = PluralPaths<Catalogue>;

/** i18next owns locale selection; a numeric count is separate from displayed arguments. */
export function translateCount(t: TFunction, key: PluralKey, count: number, args: Record<string, string | number> = {}): string {
  if (!Number.isFinite(count)) throw new RangeError('A translation count must be finite.');
  return t(key, { ...args, count });
}
