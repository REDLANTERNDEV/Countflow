/**
 * A key that survives a retry.
 *
 * Every write in the system refuses a request without one, because a handheld on
 * a flaky wireless link retries constantly and a retry that posts twice against
 * an append-only ledger is a correctness bug rather than a cosmetic one.
 *
 * The key is generated once per *intent* — one scan, one confirmation — and
 * reused for as long as that intent is being retried. Generating it inside the
 * request would make every retry a new command, which is exactly the bug the
 * header exists to prevent.
 */
export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}
