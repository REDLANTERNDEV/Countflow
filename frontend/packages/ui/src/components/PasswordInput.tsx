import { Eye, EyeOff } from 'lucide-react';
import { useState } from 'react';
import { cn } from '../lib/cn.js';
import { Input, type InputProps } from './Input.js';

export interface PasswordInputProps extends Omit<InputProps, 'type'> {
  /** Accessible name for the reveal control while the password is hidden. */
  showLabel: string;
  /** …and while it is visible. */
  hideLabel: string;
}

/**
 * A password field that can be read back.
 *
 * Endorsed rather than merely tolerated: NIST SP 800-63B asks verifiers to offer
 * a way to display the secret, on the grounds that a field nobody can check
 * pushes people towards shorter, simpler passwords and towards the clipboard.
 * It matters most exactly where this app is used — a gloved thumb on a handheld
 * in a cold aisle, where a mistyped character is invisible and the only feedback
 * is a failed sign-in that looks like a forgotten password.
 *
 * Hidden until asked, every time. The state is deliberately local and
 * unpersisted: a device on a charging rack must never come back up showing the
 * last operator's password, and an aisle is a public place.
 *
 * The labels are props because `@countflow/ui` does not depend on the catalogue
 * — the same reason `LanguageSwitcher` lives in the console rather than here.
 */
export function PasswordInput({ showLabel, hideLabel, className, ...props }: PasswordInputProps) {
  const [visible, setVisible] = useState(false);
  const Icon = visible ? EyeOff : Eye;

  return (
    <div className="relative">
      <Input
        {...props}
        type={visible ? 'text' : 'password'}
        // Room for the control, so a long password never runs underneath it.
        className={cn('pr-[var(--control-height)]', className)}
      />

      <button
        type="button"
        // Never a submit. Inside a form this is one `type` attribute away from
        // being the default button, where revealing the password would post it.
        onClick={() => setVisible((shown) => !shown)}
        aria-label={visible ? hideLabel : showLabel}
        aria-pressed={visible}
        className={cn(
          'absolute inset-y-0 right-0 grid w-[var(--control-height)] place-items-center',
          'rounded-r-[var(--radius-control)] text-[length:var(--control-text)]',
          'text-[var(--color-text-tertiary)] hover:text-[var(--color-text-primary)]',
          'transition-colors duration-[var(--duration-fast)]',
          'focus-visible:outline-2 focus-visible:outline-[var(--color-accent)]',
          'focus-visible:-outline-offset-2',
        )}
      >
        {/* Sized in `em` so it tracks the density that the control text already
            follows, rather than being 16px on a 30px control and on a 52px one. */}
        <Icon aria-hidden="true" className="h-[1.25em] w-[1.25em]" />
      </button>
    </div>
  );
}
