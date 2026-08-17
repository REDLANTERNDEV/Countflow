import * as RadixTooltip from '@radix-ui/react-tooltip';
import type { ReactNode } from 'react';

export const TooltipProvider = RadixTooltip.Provider;

export interface TooltipProps {
  /** The text to show. This is the *only* place the label exists when a rail is collapsed. */
  label: string;
  side?: 'top' | 'right' | 'bottom' | 'left';
  children: ReactNode;
}

/**
 * A label for a control that has been reduced to an icon.
 *
 * Not decoration: when the side rail is collapsed the icon is the only thing on
 * screen, and this is the label. Which is also why the trigger keeps its own
 * `aria-label` — a Radix tooltip is `aria-describedby`, so a control relying on
 * one for its *name* is unnamed to a screen reader, and pointer-only besides.
 * The tooltip is for the person who can see the icon and cannot place it.
 */
export function Tooltip({ label, side = 'right', children }: TooltipProps) {
  return (
    <RadixTooltip.Root>
      <RadixTooltip.Trigger asChild>{children}</RadixTooltip.Trigger>
      <RadixTooltip.Portal>
        <RadixTooltip.Content
          side={side}
          sideOffset={6}
          className="z-50 rounded-[var(--radius-control)]
                     border border-[var(--color-border-default)]
                     bg-[var(--color-surface-overlay)] px-[var(--space-3)] py-[var(--space-2)]
                     text-[length:var(--text-xs)] text-[var(--color-text-primary)]
                     shadow-[0_4px_16px_color-mix(in_srgb,var(--color-brand-espresso)_22%,transparent)]
                     select-none"
        >
          {label}
        </RadixTooltip.Content>
      </RadixTooltip.Portal>
    </RadixTooltip.Root>
  );
}
