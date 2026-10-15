import assert from 'node:assert/strict';
import { test } from 'node:test';
import { binCountReady, addBinObservation } from '../src/lib/bin-count.js';

test('entire-bin confirmation requires a scanned bin, claim fence and explicit empty answer', () => {
  assert.equal(binCountReady('BIN', 'BIN', 0, 'claim', [], false), false);
  assert.equal(binCountReady('BIN', 'BIN', 0, 'claim', [], true), true);
  assert.equal(binCountReady('OTHER', 'BIN', 0, 'claim', [], true), false);
  assert.equal(binCountReady('BIN', 'BIN', null, 'claim', [], true), false);
  assert.equal(binCountReady('BIN', 'BIN', 0, null, [], true), false);
  assert.equal(binCountReady('BIN', 'BIN', 0, 'claim', [{ sku: 'SKU', quantity: 1, condition: 'Available' }], false), false);
  assert.equal(binCountReady('BIN', 'BIN', 0, 'claim', [{ sku: 'SKU', quantity: 0.00001, condition: 'Available' }], true), false);
});
test('holding identity includes lot, pallet and condition; duplicate observations need correction', () => {
  const first = { sku: 'SKU', quantity: 3, condition: 'Available' as const };
  assert.equal(addBinObservation([], first)?.length, 1);
  assert.equal(addBinObservation([first], { ...first, quantity: 4 }), null);
  assert.equal(addBinObservation([first], { ...first, lotNumber: 'LOT' })?.length, 2);
  assert.equal(addBinObservation([first], { ...first, sscc: '123' })?.length, 2);
  assert.equal(addBinObservation([first], { ...first, condition: 'Quarantine' })?.length, 2);
  assert.equal(addBinObservation([], { ...first, quantity: -1 }), null);
  assert.equal(addBinObservation([], { ...first, quantity: Infinity }), null);
  assert.equal(addBinObservation([], { ...first, quantity: 0.00001 }), null);
  assert.equal(addBinObservation([], { ...first, quantity: 3.14159 }), null);
  assert.equal(addBinObservation([], { ...first, quantity: 100000000000000 }), null);
  assert.equal(addBinObservation([], { ...first, quantity: 3.1415 })?.length, 1);
  assert.equal(addBinObservation([], { ...first, sku: '' }), null);
  assert.equal(addBinObservation([], { ...first, quantity: 0 })?.length, 1);
});
