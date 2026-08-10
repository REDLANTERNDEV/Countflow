/**
 * Formatting helpers.
 *
 * The distinction they enforce is the important part: **values** get localised,
 * **identifiers** never do.
 *
 * Turkish writes `1.234,56` and `10.08.2026`, which is right for a quantity or
 * a date and catastrophic for a GTIN — `05098760010303` would render as
 * `5.098.760.010.303`, which is not a barcode and will not scan. The rule is
 * decided by what a field *means*, not by whether it happens to contain digits.
 */

/** A quantity, weight or capacity. Localised, with tabular figures at the call site. */
export function formatNumber(
  value: number | null | undefined,
  locale: string,
  options?: Intl.NumberFormatOptions,
): string {
  if (value === null || value === undefined) return '—';
  return new Intl.NumberFormat(locale, options).format(value);
}

/** A calendar date. Localised — `10.08.2026` in Turkish, `10/08/2026` in English. */
export function formatDate(
  value: string | Date | null | undefined,
  locale: string,
): string {
  if (!value) return '—';
  const date = typeof value === 'string' ? new Date(value) : value;
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(date);
}

export function formatDateTime(
  value: string | Date | null | undefined,
  locale: string,
): string {
  if (!value) return '—';
  const date = typeof value === 'string' ? new Date(value) : value;
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat(locale, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(date);
}

/**
 * An identifier — GTIN, SSCC, SKU, location code, lot number.
 *
 * Returned verbatim. This function exists so the intent is explicit at the call
 * site and nobody reaches for `formatNumber` on a barcode.
 */
export function formatIdentifier(value: string | null | undefined): string {
  return value ?? '—';
}

/**
 * Upper-cases an identifier using invariant rules.
 *
 * Under a Turkish locale `'i'.toLocaleUpperCase('tr')` is `'İ'` and
 * `'I'.toLocaleLowerCase('tr')` is `'ı'`. Location codes are upper-case
 * alphanumeric, so locale-sensitive casing would silently corrupt them for
 * Turkish users only — the worst kind of bug, because it never reproduces for
 * the person who wrote the code.
 *
 * Use this for codes. Use `toLocaleUpperCase` for display prose.
 */
export function upperCaseIdentifier(value: string): string {
  return value.toUpperCase();
}
