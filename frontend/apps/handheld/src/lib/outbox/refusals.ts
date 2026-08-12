/**
 * What a refused command *is* to the operator who made it.
 *
 * This file is the point of the whole feature. Replaying a command is a loop and
 * a header; being told what the warehouse did while you were out of signal is
 * the hard part, and an error code is not that. `work.task_already_completed`
 * means somebody else walked to the same rack; `inventory.insufficient_stock`
 * means you are holding goods the system does not believe exist. Those are two
 * different phone calls, and neither of them is "an error occurred".
 *
 * The classification exists so the screen can say three things per refusal —
 * what you did, what the warehouse says, and what to do now — rather than one.
 * A queue that silently drops refusals is worse than no queue; a queue that
 * shows them as red text with a code in it is barely better.
 */

/**
 * The kinds of news a refusal can be.
 *
 * Ordered by how urgently a person is needed, which is the order the screen
 * sorts them in.
 */
export type RefusalCategory =
  /**
   * The stock is not where the command said it was. The operator is standing
   * next to goods the record does not have, or moved goods that were not there
   * to move. This is a discrepancy, and it is the only category whose answer is
   * "go and count something".
   */
  | 'stock_missing'
  /**
   * Somebody else did the job, or it was withdrawn, between the scan and the
   * network coming back. The work happened twice or not at all, and only a
   * person looking at the bay can tell which.
   */
  | 'already_done'
  /**
   * The order this stock was promised to has let it go. The goods are real and
   * are now on a bench nobody is waiting for.
   */
  | 'claim_spent'
  /**
   * The command named something the warehouse does not have — a mistyped SKU, a
   * dock code that does not exist. Nothing was recorded, and nothing physical is
   * in question beyond where the operator actually put it.
   */
  | 'not_recognised'
  /** The operator is not allowed to do this, or their session no longer says who they are. */
  | 'not_allowed'
  /**
   * Neither accepted nor refused: repeated attempts that never got an answer.
   * The command is still real and still unsent.
   */
  | 'stalled'
  /** A code this build has never heard of. Shown with the code, because guessing is worse. */
  | 'unknown';

const CATEGORIES: Record<string, RefusalCategory> = {
  // Work — the job moved on without the operator.
  'work.task_already_completed': 'already_done',
  'work.task_not_open': 'already_done',
  'work.task_not_yours': 'already_done',
  'work.task_already_claimed': 'already_done',
  'work.task_not_found': 'already_done',

  // Inventory — the goods and the record disagree.
  'inventory.insufficient_stock': 'stock_missing',
  'inventory.insufficient_available_stock': 'claim_spent',
  'inventory.holding_not_found': 'stock_missing',
  'inventory.license_plate_empty': 'stock_missing',
  'inventory.reservation_not_open': 'claim_spent',
  'inventory.reservation_not_found': 'claim_spent',

  // The command named something that is not there. Nothing was posted.
  'inventory.item_not_found': 'not_recognised',
  'inventory.item_inactive': 'not_recognised',
  'inventory.location_not_found': 'not_recognised',
  'inventory.location_blocked': 'not_recognised',
  'inventory.lot_required': 'not_recognised',
  'inventory.lot_not_permitted': 'not_recognised',
  'inventory.lot_not_found': 'not_recognised',
  'inventory.lot_expiry_conflict': 'not_recognised',
  'inventory.quantity_not_positive': 'not_recognised',
  'inventory.invalid_sscc': 'not_recognised',
  'inventory.counted_quantity_negative': 'not_recognised',
  'inventory.cannot_count_counterparty': 'not_recognised',
  'work.location_not_found': 'not_recognised',
  'work.count_location_mismatch': 'not_recognised',
  'work.count_quantity_required': 'not_recognised',
  'inbound.item_not_found': 'not_recognised',
  'inbound.location_not_found': 'not_recognised',
  'inbound.not_a_receiving_location': 'not_recognised',
  'inbound.asn_not_found': 'not_recognised',
  'inbound.item_not_on_asn': 'not_recognised',
  'inbound.asn_not_open': 'not_recognised',
  'inbound.quantity_not_positive': 'not_recognised',

  // Authorisation, and the two invariants that refuse a movement nobody can explain.
  'auth.role_required': 'not_allowed',
  'auth.credential_required': 'not_allowed',
  'work.task_has_no_decision': 'not_allowed',
  'work.task_has_no_reservation': 'not_allowed',

  // The one refusal that means the queue itself is wrong. A key reused for a
  // *different* command is the bug this whole design exists to make impossible,
  // so it is never quietly folded into "something went wrong".
  'inventory.idempotency_key_reused': 'unknown',
};

export function categorise(code: string | undefined): RefusalCategory {
  if (!code) return 'unknown';
  return CATEGORIES[code] ?? 'unknown';
}

/**
 * Whether the refusal leaves goods in a state a supervisor should hear about.
 *
 * Drives the tone on the screen, and nothing else. `not_recognised` is excluded
 * on purpose: a mistyped SKU means nothing was posted, which is annoying rather
 * than dangerous — the operator books it again with the right code and the
 * warehouse never knew.
 */
export function needsSupervisor(category: RefusalCategory): boolean {
  return category === 'stock_missing' || category === 'already_done' || category === 'claim_spent';
}
