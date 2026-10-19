/** Counts and replenishments confirm a fixed place, including while offline. */
export function completionLocationAllowed(kind: string, scanned: string, from: string, suggested: string | null, allowOverrides = true): boolean {
  const at = scanned.trim().toUpperCase();
  if (!at) return false;
  if (kind === 'CycleCount' || kind === 'BinCount') return at === from.trim().toUpperCase();
  if (kind === 'Replenishment' || !allowOverrides) return suggested !== null && at === suggested.trim().toUpperCase();
  return true;
}
