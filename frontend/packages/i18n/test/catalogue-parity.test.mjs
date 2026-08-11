import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));

/**
 * Reads a catalogue's keys without running TypeScript.
 *
 * The catalogues are plain nested object literals, so pulling the key paths out
 * with a small parser keeps this test dependency-free and fast — it runs on
 * `node --test` with no build step, which means it can gate CI cheaply.
 */
function keyPaths(source) {
  // Strip comments and the type annotations, leaving the object literal.
  const body = source
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/^\s*\/\/.*$/gm, '')
    .replace(/^\s*import[\s\S]*?;$/gm, '')
    .replace(/^\s*export\s+(const|type)\s+\w+(\s*:\s*[\w<>.]+)?\s*=\s*/m, '')
    .replace(/\s*as const;?\s*$/, '')
    .replace(/;\s*$/, '');

  const paths = [];
  const stack = [];
  const lines = body.split('\n');

  for (const raw of lines) {
    const line = raw.trim();
    if (!line || line === '}' || line === '},' || line === '{') {
      if (line.startsWith('}')) stack.pop();
      continue;
    }

    // A key is either a bare identifier or a fully quoted string. Matching an
    // *optional* leading quote would also match the continuation line of a
    // wrapped value — `'Clean: {{holdings}} holdings…'` reads as a key named
    // `Clean`, and the phantom key then shows up as a missing translation in
    // whichever language happened not to wrap that string.
    const key = (line) => {
      const match = line.match(/^(?:'([\w.]+)'|([A-Za-z_$][\w$]*))\s*:/);
      return match ? (match[1] ?? match[2]) : null;
    };

    if (/:\s*\{$/.test(line)) {
      const name = key(line);
      if (name) {
        stack.push(name);
        continue;
      }
    }

    const leaf = key(line);
    if (leaf) {
      paths.push([...stack, leaf].join('.'));
    }
  }

  return paths.sort();
}

const en = keyPaths(readFileSync(join(here, '../src/locales/en.ts'), 'utf8'));
const tr = keyPaths(readFileSync(join(here, '../src/locales/tr.ts'), 'utf8'));

test('the catalogues are not empty', () => {
  assert.ok(en.length > 50, `expected a substantial English catalogue, got ${en.length} keys`);
});

test('every English key exists in Turkish', () => {
  // A missing key does not throw at runtime — i18next falls back to English. So
  // without this check a Turkish operator quietly gets English strings, which is
  // exactly the failure this whole exercise is meant to prevent.
  const missing = en.filter((key) => !tr.includes(key));
  assert.deepEqual(missing, [], `missing Turkish translations: ${missing.join(', ')}`);
});

test('Turkish has no keys English lacks', () => {
  // The other direction catches stale entries left behind after a rename.
  const orphaned = tr.filter((key) => !en.includes(key));
  assert.deepEqual(orphaned, [], `orphaned Turkish keys: ${orphaned.join(', ')}`);
});

test('interpolation placeholders match between languages', () => {
  // `{{sku}}` renamed in one catalogue and not the other renders as a literal
  // `{{sku}}` on screen rather than failing loudly.
  const placeholders = (source) => {
    const map = new Map();
    const pattern = /'?([\w.]+)'?\s*:\s*(['"`])((?:\\.|(?!\2)[\s\S])*)\2/g;
    let match;
    while ((match = pattern.exec(source)) !== null) {
      const vars = [...match[3].matchAll(/\{\{(\w+)\}\}/g)].map((m) => m[1]).sort();
      if (vars.length > 0) map.set(match[1], vars.join(','));
    }
    return map;
  };

  const enVars = placeholders(readFileSync(join(here, '../src/locales/en.ts'), 'utf8'));
  const trVars = placeholders(readFileSync(join(here, '../src/locales/tr.ts'), 'utf8'));

  const mismatched = [];
  for (const [key, vars] of enVars) {
    if (trVars.has(key) && trVars.get(key) !== vars) {
      mismatched.push(`${key}: en(${vars}) vs tr(${trVars.get(key)})`);
    }
  }

  assert.deepEqual(mismatched, [], `placeholder mismatch: ${mismatched.join('; ')}`);
});
