import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import type { ReactNode } from 'react';
import { cn } from '../lib/cn.js';

export interface SheetProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /**
   * The control that opens the panel, rendered in place.
   *
   * Passed in rather than wired up by the caller, and that is the whole reason
   * this prop exists: Radix returns focus to `Dialog.Trigger` when the panel
   * closes, and it has nothing to return focus *to* if the opener is some other
   * button that merely flipped `open`. Built that way first, and a keyboard user
   * pressing Escape landed on `<body>` and had to tab from the top of the
   * document to get back to where they were.
   */
  trigger: ReactNode;
  /** Accessible name for the panel. Required — a dialog without one is unlabelled to a screen reader. */
  title: string;
  /** Label for the close button, so the only wording in here is still translatable. */
  closeLabel: string;
  side?: 'left' | 'right';
  children: ReactNode;
  className?: string;
}

/**
 * A panel that slides in from the edge, over the page.
 *
 * Radix Dialog rather than a hand-rolled `<div>`, and this is the case the
 * `Select` note describes as Radix earning its place: a drawer has to trap focus,
 * restore it to whatever opened it, close on Escape, mark the rest of the page
 * `aria-hidden`, and stop the body scrolling underneath. Every one of those is a
 * thing hand-rolled drawers get wrong, and getting them wrong on the navigation
 * means a keyboard user can tab into a menu they cannot leave.
 */
export function Sheet({
  open,
  onOpenChange,
  trigger,
  title,
  closeLabel,
  side = 'left',
  children,
  className,
}: SheetProps) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Trigger asChild>{trigger}</Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay
          className="fixed inset-0 z-40 bg-[color-mix(in_srgb,var(--color-brand-espresso)_55%,transparent)]
                     backdrop-blur-[2px]
                     data-[state=open]:animate-[sheet-fade_var(--duration-fast)_var(--ease-standard)]"
        />
        <Dialog.Content
          className={cn(
            'fixed inset-y-0 z-50 flex w-[min(82vw,300px)] flex-col',
            'border-[var(--color-border-default)] bg-[var(--color-surface-raised)]',
            'shadow-[0_0_40px_color-mix(in_srgb,var(--color-brand-espresso)_28%,transparent)]',
            'focus:outline-none',
            side === 'left' ? 'left-0 border-r' : 'right-0 border-l',
            side === 'left'
              ? 'data-[state=open]:animate-[sheet-in-left_var(--duration-standard)_var(--ease-standard)]'
              : 'data-[state=open]:animate-[sheet-in-right_var(--duration-standard)_var(--ease-standard)]',
            className,
          )}
        >
          <div
            className="flex shrink-0 items-center justify-between gap-[var(--space-3)]
                       border-b border-[var(--color-border-subtle)]
                       px-[var(--space-4)] py-[var(--space-3)]"
          >
            <Dialog.Title
              className="m-0 text-[length:var(--text-xs)] font-semibold uppercase
                         tracking-[var(--tracking-caption)] text-[var(--color-text-tertiary)]"
            >
              {title}
            </Dialog.Title>

            <Dialog.Close
              aria-label={closeLabel}
              // 44px: this control only ever exists on a viewport narrow enough to
              // be a touchscreen, and the console density is sized for a mouse.
              className="inline-flex h-[44px] w-[44px] items-center justify-center
                         rounded-[var(--radius-control)] text-[var(--color-text-secondary)]
                         transition-colors duration-[var(--duration-fast)]
                         hover:bg-[var(--color-surface-hover)] hover:text-[var(--color-text-primary)]
                         focus-visible:outline-2 focus-visible:outline-[var(--color-accent)]"
            >
              <X size={18} aria-hidden="true" />
            </Dialog.Close>
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain p-[var(--space-3)]">
            {children}
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
