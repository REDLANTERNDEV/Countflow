import { useEffect, useRef, useState, type ChangeEvent, type KeyboardEvent, type RefObject } from 'react';
import { cn } from '../lib/cn.js';
import { acceptsTypedInput } from '../lib/focus.js';
import { Label } from './Input.js';

export interface ScanFieldProps {
  label: string;
  placeholder?: string;
  disabled?: boolean;
  /** Fires on Enter — which is what a keyboard-wedge scanner sends after the payload. */
  onScan: (value: string) => void;
  /** Keeps focus in the field, so an operator never has to tap before scanning. */
  autoFocus?: boolean;
  /** Clear the field after a scan. Off for fields the operator should be able to review. */
  clearOnScan?: boolean;
  /** A sibling camera control can return focus to this scan target. */
  inputRef?: RefObject<HTMLInputElement | null>;
}

/**
 * Input tuned for handheld barcode scanners.
 *
 * Scanners in keyboard-wedge mode behave like a very fast keyboard: they type
 * the payload in a few milliseconds and finish with Enter. Two things follow
 * that a normal text input gets wrong:
 *
 *   1. Focus must be sticky. An operator holding a scanner in one hand and a
 *      carton in the other cannot tap the field first, so we take focus back
 *      whenever it is lost.
 *   2. The value is an identifier, not prose — monospace, never locale-formatted,
 *      and no autocorrect or autocapitalisation anywhere near it.
 */
export function ScanField(props: ScanFieldProps) {
  const { label, placeholder, disabled = false, onScan, autoFocus = true, clearOnScan = true } = props;

  const ownInputRef = useRef<HTMLInputElement>(null);
  const inputRef = props.inputRef ?? ownInputRef;
  const [value, setValue] = useState('');
  const id = `scan-${label.replace(/\s+/g, '-').toLowerCase()}`;

  useEffect(() => {
    if (!autoFocus || disabled) return;

    const input = inputRef.current;
    input?.focus();

    // Refocus after anything *steals* focus (a toast, an incidental tap, a
    // button press) — but not when the operator has deliberately moved it into
    // another field. Taking it back unconditionally made every input beside a
    // scan field impossible to type in: the password on the sign-in screen, the
    // quantity and dock on receiving, the override reason on a task. Focus
    // landed there and was pulled out again a tick later, which reads as the
    // field simply not accepting taps. See `acceptsTypedInput`.
    const onFocusOut = (event: FocusEvent) => {
      // Reading help is intentional keyboard navigation, not a lost scan target.
      if ((event.relatedTarget as HTMLElement | null)?.closest('[data-info-help], [data-scan-control], [role="dialog"]')) return;
      if (acceptsTypedInput(event.relatedTarget as HTMLElement | null)) return;

      window.setTimeout(() => {
        if (!disabled) inputRef.current?.focus();
      }, 0);
    };

    input?.addEventListener('focusout', onFocusOut);
    return () => input?.removeEventListener('focusout', onFocusOut);
  }, [autoFocus, disabled, inputRef]);

  function handleChange(event: ChangeEvent<HTMLInputElement>) {
    setValue(event.target.value);
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key !== 'Enter') return;

    event.preventDefault();

    const scanned = value.trim();
    if (scanned.length === 0) return;

    onScan(scanned);
    if (clearOnScan) setValue('');
  }

  return (
    <div className="flex flex-col gap-[var(--space-2)]">
      <Label htmlFor={id}>{label}</Label>
      <input
        id={id}
        ref={inputRef}
        value={value}
        placeholder={placeholder}
        disabled={disabled}
        autoComplete="off"
        autoCorrect="off"
        autoCapitalize="off"
        spellCheck={false}
        // Numeric-friendly keypad without forcing it: most barcodes are digits,
        // but lot numbers are alphanumeric so a strict numeric input would trap
        // the operator.
        inputMode="text"
        onChange={handleChange}
        onKeyDown={handleKeyDown}
        className={cn(
          'w-full rounded-[var(--radius-control)] border border-[var(--color-border-default)]',
          'bg-[var(--color-surface-sunken)] text-[var(--color-text-primary)]',
          'h-[var(--control-height)] px-[var(--space-3)]',
          'font-[family-name:var(--font-mono)] tabular-nums',
          'text-[length:var(--control-text)]',
          'placeholder:text-[var(--color-text-tertiary)] placeholder:font-[family-name:var(--font-sans)]',
          'transition-colors duration-[var(--duration-fast)]',
          'focus:outline-2 focus:outline-[var(--color-accent)] focus:-outline-offset-1',
          'disabled:opacity-45',
        )}
      />
    </div>
  );
}
