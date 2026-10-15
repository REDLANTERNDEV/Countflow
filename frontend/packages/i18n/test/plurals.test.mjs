import assert from 'node:assert/strict';
import { test } from 'node:test';
import i18next from 'i18next';
import { en } from '../src/locales/en.ts';
import { tr } from '../src/locales/tr.ts';
import { registerHooks } from 'node:module';
import { catalogueEntries } from './catalogues.js';

// Node erases types, but source imports deliberately keep their emitted .js suffix.
registerHooks({ resolve(specifier, context, next) {
  if (specifier.startsWith('.') && specifier.endsWith('.js')) {
    try { return next(`${specifier.slice(0, -3)}.ts`, context); } catch { /* It may be real JavaScript. */ }
  }
  return next(specifier, context);
} });

async function translator(lng) {
  const instance = i18next.createInstance();
  await instance.init({ lng, load: 'languageOnly', fallbackLng: 'en', resources: { en: { translation: en }, tr: { translation: tr } }, interpolation: { escapeValue: false } });
  return instance.t.bind(instance);
}

test('existing count callers select singular and plural in each locale', async () => {
  for (const lng of ['en', 'en-GB']) {
    const t = await translator(lng);
    assert.equal(t('queue.needsYou', { count: 1 }), '1 needs you');
    assert.equal(t('queue.needsYou', { count: 2 }), '2 need you');
    assert.equal(t('items.shelfLifeDays', { count: 1 }), '1 day');
    assert.equal(t('items.shelfLifeDays', { count: 0 }), '0 days');
    assert.equal(t('queue.attempts', { count: 1 }), 'Tried 1 time');
    assert.equal(t('pickTours.resume', { count: 1 }), 'Resume pick tour · 1 stop');
    assert.equal(t('outbound.waves.orders', { count: 1 }), '1 order');
  }
  for (const lng of ['tr', 'tr-TR']) {
    const t = await translator(lng);
    assert.equal(t('items.shelfLifeDays', { count: 1 }), '1 gün');
    assert.equal(t('items.shelfLifeDays', { count: 2 }), '2 gün');
  }
});

test('typed helper delegates cardinal rules and refuses nonfinite counts', async () => {
  const { translateCount } = await import('../src/plurals.ts'); const t = await translator('en');
  assert.equal(translateCount(t, 'items.shelfLifeDays', 1), '1 day');
  assert.equal(translateCount(t, 'items.shelfLifeDays', 1.5), '1.5 days');
  assert.equal(translateCount(t, 'items.shelfLifeDays', -1), '-1 day');
  assert.equal(translateCount(t, 'queue.needsYou', 1, { count: 2 }), '1 needs you');
  assert.throws(() => translateCount(t, 'items.shelfLifeDays', NaN), RangeError);
  assert.throws(() => translateCount(t, 'items.shelfLifeDays', Infinity), RangeError);
});

test('all plural families supply both cardinal forms with matching placeholders', () => {
  const english = catalogueEntries(en); const turkish = catalogueEntries(tr);
  const families = Object.keys(english).filter(key => key.endsWith('_one')); assert.ok(families.length >= 12);
  for (const one of families) {
    const other = one.replace(/_one$/, '_other');
    assert.equal(typeof english[other], 'string', other); assert.equal(typeof turkish[one], 'string', one); assert.equal(typeof turkish[other], 'string', other);
    const placeholders = value => [...value.matchAll(/\{\{(\w+)\}\}/g)].map(match => match[1]).sort();
    assert.deepEqual(placeholders(english[one]), placeholders(english[other]), one);
    assert.deepEqual(placeholders(english[one]), placeholders(turkish[one]), one);
  }
});

test('rule notes select day/hour forms from server strings without dropping named arguments', async () => {
  const { translateRuleNote } = await import('../src/rules.ts'); const t = await translator('en');
  assert.equal(translateRuleNote(t, { code: 'rules.allocation.expires_on', args: { expiry: '2027-01-15', days: '1' } }), 'Expires 2027-01-15, in 1 day.');
  assert.equal(translateRuleNote(t, { code: 'rules.count.recently_moved', args: { hours: '1', required: '24' } }), 'Worked 1 hour ago; it has to stand for 24.');
  assert.equal(translateRuleNote(t, { code: 'rules.count.counted_recently', args: { days: '2', required: '7' } }), 'Counted 2 days ago, and 7 has to pass first.');
  assert.equal(translateRuleNote(t, { code: 'rules.count.days_since_count', args: { days: '1', target: '7' } }), 'Checked 1 day ago, against a 7-day target.');
  assert.equal(translateRuleNote(t, { code: 'rules.disposition.days_since_despatch', args: { days: '1', window: '30' } }), 'Back after 1 day, against a 30-day window.');
  assert.equal(translateRuleNote(t, { code: 'rules.allocation.expires_on', args: { expiry: '2027-01-15', days: 'unknown' } }), 'Expires 2027-01-15, in unknown days.');
  const turkish = await translator('tr');
  assert.ok(translateRuleNote(turkish, { code: 'rules.allocation.expires_on', args: { expiry: '2027-01-15', days: '1' } }).includes('1 gün'));
});
