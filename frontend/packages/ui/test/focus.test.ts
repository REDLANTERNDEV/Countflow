import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { acceptsTypedInput } from '../src/lib/focus.js';

/**
 * The rule that keeps a sticky scan field from swallowing the fields beside it.
 *
 * `ScanField` holds focus for the scanner, and for most of this project's life
 * it held focus from everything: the sign-in password, receiving's quantity and
 * dock, a task's override reason. Focus reached them and was pulled back a tick
 * later, so they read as inputs that ignored taps. Prose in the component said
 * "refocus after anything steals focus" and that is exactly what it did.
 */

const here = dirname(fileURLToPath(import.meta.url));

test('focus stays where the operator deliberately put it', () => {
  // The bug, as the sign-in screen met it.
  assert.equal(acceptsTypedInput({ tagName: 'INPUT', type: 'password' }), true);
  assert.equal(acceptsTypedInput({ tagName: 'INPUT', type: 'text' }), true);
  assert.equal(acceptsTypedInput({ tagName: 'TEXTAREA' }), true);
  assert.equal(acceptsTypedInput({ tagName: 'SELECT' }), true);
  assert.equal(acceptsTypedInput({ isContentEditable: true }), true);

  // An `<input>` with no type attribute is a text input.
  assert.equal(acceptsTypedInput({ tagName: 'INPUT' }), true);
  assert.equal(acceptsTypedInput({ tagName: 'input', type: 'PASSWORD' }), true);
});

test('focus comes back from anything a scan cannot be typed into', () => {
  // Null is focus going nowhere — an incidental tap on the page — which is the
  // case the sticky behaviour exists for.
  assert.equal(acceptsTypedInput(null), false);
  assert.equal(acceptsTypedInput(undefined), false);

  // The case that decides the shape of the rule: a button is focusable, so a
  // "did focus move at all" test would leave it holding focus, and the Enter
  // that ends every scan would press it.
  assert.equal(acceptsTypedInput({ tagName: 'BUTTON' }), false);
  assert.equal(acceptsTypedInput({ tagName: 'INPUT', type: 'submit' }), false);
  assert.equal(acceptsTypedInput({ tagName: 'INPUT', type: 'checkbox' }), false);
  assert.equal(acceptsTypedInput({ tagName: 'INPUT', type: 'radio' }), false);
  assert.equal(acceptsTypedInput({ tagName: 'INPUT', type: 'file' }), false);
  assert.equal(acceptsTypedInput({ tagName: 'A' }), false);
  assert.equal(acceptsTypedInput({ tagName: 'DIV' }), false);
});

test('ScanField actually consults the rule before reclaiming focus', () => {
  // The predicate above can be perfectly right while the component ignores it,
  // which is the state this file was written to end. Asserted against the
  // source because the alternative is a DOM and a component renderer for one
  // branch of one effect.
  const source = readFileSync(join(here, '..', 'src', 'components', 'ScanField.tsx'), 'utf8');

  assert.match(
    source,
    /acceptsTypedInput\(\s*event\.relatedTarget/,
    'ScanField no longer checks where focus went before taking it back — the ' +
      'fields beside a scan field are unreachable again',
  );
});
