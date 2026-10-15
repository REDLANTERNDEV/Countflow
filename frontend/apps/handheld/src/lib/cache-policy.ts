interface Cached { id: string; fetchedAt: string; detail: unknown }
/** A history limit must never evict instructions for outstanding physical work. */
export function evictableTaskIds(rows: Cached[], protectedIds: string[], limit: number): string[] {
  const protectedSet = new Set(protectedIds);
  const protectedRows = rows.filter(row => protectedSet.has(row.id)
    || (row.detail as { task: { status: string } }).task.status === 'Assigned');
  const kept = new Set(protectedRows.map(row => row.id));
  return rows.filter(row => !kept.has(row.id))
    .sort((a, b) => b.fetchedAt.localeCompare(a.fetchedAt))
    .slice(Math.max(0, limit - protectedRows.length)).map(row => row.id);
}
