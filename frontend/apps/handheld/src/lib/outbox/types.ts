/**
 * What the device is holding on the operator's behalf.
 *
 * A queued command is evidence of physical work. Somebody walked to a rack, put
 * a pallet down and scanned the label; the only difference between that and a
 * confirmed putaway is whether a radio was working at the time. That is the
 * frame for every decision in this folder — in particular why a pending command
 * cannot be discarded from the handheld, and why a refusal is news rather than
 * an error.
 */

/**
 * The published endpoints the queue may drive.
 *
 * Deliberately a closed union rather than a URL and a body. The queue replays
 * *operations the operator performed*, and every one of them is an assertion of
 * fact: "I booked this off the truck", "I confirmed this job here". Nothing that
 * asks the server to *decide* something is queueable — claim-next hands out work
 * and needs the queue it is handing out from, so a device that answered it
 * locally would give two operators the same job. See ADR 0008.
 */
export type CommandKind = 'work.complete' | 'inbound.receipt';

/** The body of `POST /work/tasks/{id}/complete`, exactly as the endpoint takes it. */
export interface CompleteCommandRequest {
  taskId: string;
  toLocationCode: string;
  overrideReason?: string;
  countedQuantity?: number;
}

/** The body of `POST /inbound/receipts`, exactly as the endpoint takes it. */
export interface ReceiptCommandRequest {
  sku: string;
  quantity: number;
  toLocationCode: string;
  sscc?: string;
  asnReference?: string;
}

export type CommandRequest = CompleteCommandRequest | ReceiptCommandRequest;

/**
 * What the operator did, in the words they would use.
 *
 * Snapshotted when the command is enqueued, and this is not redundancy. If the
 * server refuses the command there is no server-side record of it to look
 * anything up from — the device is the only thing in the world that knows a
 * person carried 24 cases of SKU-10001 to A01-01-02-A at 14:32. A reconciliation
 * screen that could only show an error code would be telling an operator that
 * something they cannot identify went wrong.
 */
export interface CommandSummary {
  /** `work.kind.Putaway`, `work.kind.Pick`, `work.kind.CycleCount`, `queue.kind.receipt`. */
  headline: string;
  sku: string;
  description: string;
  quantity: number;
  uom: string;
  /** Where the goods were taken from, or the bay a count was about. */
  fromLocationCode?: string;
  /** The label the operator actually scanned. */
  atLocationCode?: string;
  lotNumber?: string;
  sscc?: string;
  /** The order a pick was for; the advice note a receipt was against. */
  reference?: string;
  /** What a count reported. */
  countedQuantity?: number;
}

/**
 * Where a command is in its life.
 *
 * `sending` is the only state that is not durable in meaning: on start-up
 * anything left in it goes back to `pending`, because a device that died
 * mid-request cannot know whether the request landed. Retrying is safe — that is
 * the entire point of the idempotency key, and the reason this record is keyed
 * *by* the key rather than merely carrying one.
 */
export type CommandState =
  /** Waiting for a network. */
  | 'pending'
  /** In flight right now. */
  | 'sending'
  /** The server took it. Kept for a while so the operator can see their scans landed. */
  | 'settled'
  /** The server answered, and the answer was no. Needs a person. */
  | 'refused'
  /** Neither accepted nor refused after repeated tries. Needs a person for a different reason. */
  | 'stalled';

export interface CommandRefusal {
  /** The coded `Error` from the API. The queue never stores prose; see the i18n catalogue. */
  code: string;
  status: number;
  at: string;
}

export interface QueuedCommand {
  /**
   * The idempotency key, and the primary key of the record.
   *
   * One value, doing both jobs, on purpose. A key held in a field beside the
   * record is a key some future restore path can forget to carry; a key that
   * *is* the record's identity cannot be regenerated without producing a
   * different command, which is exactly the property the ledger needs.
   */
  id: string;
  /** Monotonic within the device. The order the work actually happened in. */
  seq: number;
  kind: CommandKind;
  /**
   * The operator whose credentials may send this, and whose name the ledger will
   * carry. Not a preference: replaying OP001's putaway on OP002's token writes a
   * false actor into an append-only record that is never corrected, only
   * compensated. See ADR 0008.
   */
  ownerUserId: string;
  ownerOperatorCode: string;
  createdAt: string;
  request: CommandRequest;
  summary: CommandSummary;
  state: CommandState;
  attempts: number;
  lastAttemptAt?: string;
  /** The success payload, so a scan that landed while nobody was looking can still be read back. */
  result?: unknown;
  refusal?: CommandRefusal;
  /** Set when the operator has seen a refusal or a stall and taken it on. */
  acknowledgedAt?: string;
}

/** A task as the device last saw it, so a claimed job can be worked out of signal. */
export interface CachedTask {
  id: string;
  fetchedAt: string;
  ownerUserId: string;
  /** The `WorkTaskDetailResponse` payload, stored whole. */
  detail: unknown;
}
