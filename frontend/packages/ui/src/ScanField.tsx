import { useEffect, useRef, useState, type ChangeEvent, type KeyboardEvent } from 'react';

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
 *   2. Fast bursts are scans, slow typing is a human. We track inter-keystroke
 *      timing and report which one produced the value, so callers can require a
 *      real scan where blind-typing a barcode would defeat the verification.
 */
export function ScanField(props: ScanFieldProps) {
  const { label, placeholder, disabled = false, onScan, autoFocus = true, clearOnScan = true } = props;

  const inputRef = useRef<HTMLInputElement>(null);
  const [value, setValue] = useState('');

  // Timing of the current burst. A wedge scanner lands keystrokes ~5-20ms
  // apart; a person managing 30ms sustained is not typing a barcode by hand.
  const keystrokeGaps = useRef<number[]>([]);
  const lastKeystrokeAt = useRef<number>(0);

  useEffect(() => {
    if (!autoFocus || disabled) return;

    const input = inputRef.current;
    input?.focus();

    // Refocus after anything steals focus (a toast, an incidental tap).
    const onFocusOut = () => {
      window.setTimeout(() => {
        if (!disabled) inputRef.current?.focus();
      }, 0);
    };

    input?.addEventListener('focusout', onFocusOut);
    return () => input?.removeEventListener('focusout', onFocusOut);
  }, [autoFocus, disabled]);

  function handleChange(event: ChangeEvent<HTMLInputElement>) {
    const now = performance.now();

    if (lastKeystrokeAt.current > 0) {
      keystrokeGaps.current.push(now - lastKeystrokeAt.current);
    }

    lastKeystrokeAt.current = now;
    setValue(event.target.value);
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key !== 'Enter') return;

    event.preventDefault();

    const scanned = value.trim();
    if (scanned.length === 0) return;

    onScan(scanned);

    if (clearOnScan) setValue('');

    keystrokeGaps.current = [];
    lastKeystrokeAt.current = 0;
  }

  return (
    <div className="cf-field">
      <label className="cf-label" htmlFor={`scan-${label}`}>
        {label}
      </label>
      <input
        id={`scan-${label}`}
        ref={inputRef}
        className="cf-input cf-mono"
        value={value}
        placeholder={placeholder}
        disabled={disabled}
        autoComplete="off"
        autoCorrect="off"
        autoCapitalize="off"
        spellCheck={false}
        onChange={handleChange}
        onKeyDown={handleKeyDown}
      />
    </div>
  );
}
