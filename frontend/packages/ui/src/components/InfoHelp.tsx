import * as RadixTooltip from '@radix-ui/react-tooltip';
import { Info } from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { acceptsTypedInput } from '../lib/focus.js';

export interface InfoHelpProps {
  label: string;
  children: ReactNode;
}

/** Optional explanation, available to a pointer, a keyboard, or a touch screen. */
export function InfoHelp({ label, children }: InfoHelpProps) {
  const [temporary, setTemporary] = useState(false);
  const [pinned, setPinned] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const content = useRef<HTMLDivElement>(null);
  const previousFocus = useRef<HTMLElement | null>(null);
  const open = !dismissed && (temporary || pinned);
  function dismiss() {
    const focused = document.activeElement;
    if (focused === trigger.current || content.current?.contains(focused)) {
      const restore = previousFocus.current?.isConnected ? previousFocus.current : trigger.current;
      restore?.focus();
    }
    setPinned(false); setTemporary(false); setDismissed(true);
  }
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      const target = event.target as Node;
      if (!trigger.current?.contains(target) && !content.current?.contains(target)) {
        setPinned(false); setTemporary(false); setDismissed(true);
        // Let the click choose its normal target first. A tap on blank space
        // should return to scanning; a tap into another control must keep it.
        requestAnimationFrame(() => {
          const focused = document.activeElement;
          if ((focused === document.body || focused === trigger.current || content.current?.contains(focused))
              && previousFocus.current?.isConnected) previousFocus.current.focus();
        });
      }
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        dismiss();
      }
    };
    document.addEventListener('pointerdown', outside, true);
    document.addEventListener('keydown', escape, true);
    return () => {
      document.removeEventListener('pointerdown', outside, true);
      document.removeEventListener('keydown', escape, true);
    };
  }, [open]);
  return <RadixTooltip.Provider delayDuration={250} disableHoverableContent={false}>
    <RadixTooltip.Root open={open} onOpenChange={setTemporary}>
      <RadixTooltip.Trigger asChild>
        <button ref={trigger} type="button" data-info-help aria-label={label} aria-expanded={open}
          className="inline-flex min-h-[44px] min-w-[44px] shrink-0 items-center justify-center rounded-[var(--radius-control)]
            text-[var(--color-text-tertiary)] hover:text-[var(--color-text-primary)]
            focus-visible:outline-2 focus-visible:outline-[var(--color-accent)]"
          onPointerEnter={() => setDismissed(false)} onBlur={() => { if (!pinned) setTemporary(false); }}
          onFocus={(event) => {
            if (event.relatedTarget instanceof HTMLElement && acceptsTypedInput(event.relatedTarget))
              previousFocus.current = event.relatedTarget;
            setDismissed(false);
          }}
          onClick={() => { if (pinned) dismiss(); else { setDismissed(false); setPinned(true); } }}>
          <Info size={16} strokeWidth={1.5} aria-hidden="true" />
        </button>
      </RadixTooltip.Trigger>
      <RadixTooltip.Portal>
        <RadixTooltip.Content ref={content} data-info-help tabIndex={0} side="bottom" sideOffset={4} collisionPadding={12}
          onEscapeKeyDown={dismiss}
          className="z-50 w-max max-w-[min(320px,calc(100vw-24px))] max-h-[min(360px,var(--radix-tooltip-content-available-height))]
            overflow-y-auto whitespace-normal break-words
            rounded-[var(--radius-control)] border border-[var(--color-border-default)]
            bg-[var(--color-surface-overlay)] px-[var(--space-4)] py-[var(--space-3)]
            text-[length:var(--text-sm)] font-normal leading-relaxed text-[var(--color-text-primary)]
            shadow-[0_4px_16px_color-mix(in_srgb,var(--color-brand-espresso)_22%,transparent)]">
          {children}
        </RadixTooltip.Content>
      </RadixTooltip.Portal>
    </RadixTooltip.Root>
  </RadixTooltip.Provider>;
}
