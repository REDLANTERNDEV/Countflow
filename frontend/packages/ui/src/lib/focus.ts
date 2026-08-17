/**
 * Where focus went, and whether a sticky field is entitled to take it back.
 *
 * A scan field holds focus so an operator with a scanner in one hand and a
 * carton in the other never has to tap first (see `ScanField`). Held
 * unconditionally, that is a trap: every screen that has a scan field also has
 * something to type into beside it — a password, a quantity, a dock, an
 * override reason — and a field that grabs focus back on every `focusout`
 * makes all of them unreachable.
 *
 * The distinction that makes both work is *what* focus moved to. Focus that
 * went nowhere (an incidental tap on the page, a toast that took and released
 * it) leaves `relatedTarget` null and should come back. Focus the operator
 * deliberately put in another text field should stay where they put it.
 *
 * Buttons are the case that decides the shape of this: they are focusable but
 * cannot be typed into, so leaving focus on one points the scanner at a control
 * rather than a field — the payload goes nowhere and the Enter that ends every
 * scan presses the button. So the question is not "is it focusable" but "can it
 * receive what a scanner types".
 */

/**
 * `<input>` types that take a click rather than a keyboard payload. Everything
 * else an `<input>` can be — text, password, search, number, the date family —
 * accepts typing, so the exclusion list is the short side of the fence.
 */
const NON_TEXT_INPUT_TYPES = new Set([
  'button',
  'checkbox',
  'color',
  'file',
  'image',
  'radio',
  'range',
  'reset',
  'submit',
]);

/** The parts of an element this needs, so the rule is testable without a DOM. */
export interface TypingTarget {
  tagName?: string;
  type?: string;
  isContentEditable?: boolean;
}

/**
 * Whether `node` can receive typed characters — including the burst a
 * keyboard-wedge scanner sends.
 */
export function acceptsTypedInput(node: TypingTarget | null | undefined): boolean {
  if (!node) return false;
  if (node.isContentEditable) return true;

  const tag = node.tagName?.toUpperCase();
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
  if (tag !== 'INPUT') return false;

  // `.type` on a real `<input>` reflects the resolved type, so the fallback is
  // only for the plain objects the tests describe elements with.
  return !NON_TEXT_INPUT_TYPES.has((node.type ?? 'text').toLowerCase());
}
