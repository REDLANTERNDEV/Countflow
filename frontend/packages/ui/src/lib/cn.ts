import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

/**
 * Merges class names, with later Tailwind utilities winning over earlier ones
 * in the same group.
 *
 * Plain concatenation leaves both `px-2` and `px-4` in the class list and lets
 * CSS source order decide — which means a caller cannot reliably override a
 * component's padding. `twMerge` resolves the conflict by intent instead.
 */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
