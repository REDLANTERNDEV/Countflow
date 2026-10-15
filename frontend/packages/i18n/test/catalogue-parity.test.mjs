import assert from 'node:assert/strict';
import { test } from 'node:test';
import { en } from '../src/locales/en.ts';
import { tr } from '../src/locales/tr.ts';
import { catalogueEntries } from './catalogues.js';

// Node 24's type erasure lets us inspect the real catalogues without a build.
const english = catalogueEntries(en); const turkish = catalogueEntries(tr);
const enKeys = Object.keys(english).sort(); const trKeys = Object.keys(turkish).sort();

test('the catalogues are not empty', () => {
  assert.ok(enKeys.length > 50);
});
test('every English key exists in Turkish', () => {
  assert.deepEqual(enKeys.filter(key => !(key in turkish)), []);
});
test('Turkish has no keys English lacks', () => {
  assert.deepEqual(trKeys.filter(key => !(key in english)), []);
});
test('interpolation placeholders match between languages', () => {
  const placeholders = value => [...value.matchAll(/\{\{(\w+)\}\}/g)].map(match => match[1]).sort();
  for (const key of enKeys) assert.deepEqual(placeholders(english[key]), placeholders(turkish[key]), key);
});
