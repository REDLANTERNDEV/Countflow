import type { OutboxStore } from './store.js';
import { SETTLED_KEPT } from './store.js';
import type {
  CommandKind,
  CommandRequest,
  CommandSummary,
  QueuedCommand,
} from './types.js';

/**
 * What came back from trying to send a command.
 *
 * The distinction that matters is between *an answer* and *no answer*. A coded
 * refusal is an answer: the warehouse considered the command and said no, and
 * sending it again would get the same no. A transport failure is not an answer,
 * and the command is exactly as valid as it was a second ago.
 *
 * `navigator.onLine` does not appear anywhere in this decision, and that is
 * deliberate. It says whether the device has a link, not whether the warehouse
 * is reachable over it — a handheld sitting on a working access point in front
 * of an API that is down reports itself online, and a queue that trusted the
 * flag would sit there believing it had nothing to do. The flag is a hint about
 * *when to try*, never about what happened.
 */
export type SendOutcome =
  | { readonly kind: 'accepted'; readonly result: unknown }
  /** The server answered with a coded refusal. Terminal. */
  | { readonly kind: 'refused'; readonly code: string; readonly status: number }
  /** No answer at all — no network, DNS, a dropped socket. */
  | { readonly kind: 'unreachable' }
  /** The server answered, but with a fault of its own. An answer about the server, not the command. */
  | { readonly kind: 'unwell'; readonly status: number }
  /** The session is gone. Nothing can be sent until somebody signs in. */
  | { readonly kind: 'unauthenticated' };

export type Send = (command: QueuedCommand) => Promise<SendOutcome>;

export interface Owner {
  userId: string;
  operatorCode: string;
}

export interface OutboxSnapshot {
  commands: QueuedCommand[];
  draining: boolean;
  /** What the last completed drain did, so the shell can say "3 sent" once and stop. */
  lastDrain: { sent: number; refused: number } | null;
  /**
   * Whether the warehouse answered the last time anything asked it, or `null`
   * before anything has.
   *
   * This is what the header badge reports, in preference to `navigator.onLine`.
   * The flag says the radio is on; an operator needs to know whether the
   * warehouse can hear them, and the two differ in both directions — an access
   * point with a dead API behind it reads as online, and the API behind a reverse
   * proxy answers 502 rather than failing to answer at all.
   */
  reachable: boolean | null;
}

export interface SubmitInput {
  kind: CommandKind;
  /** The idempotency key. Generated once per intent by the screen, and never again. */
  key: string;
  request: CommandRequest;
  summary: CommandSummary;
}

/**
 * How many times a command may fail to get an answer before the device stops
 * letting it hold the queue up.
 *
 * A command that has been tried this many times without either landing or being
 * refused is not a network problem any more — it is a fault, and the honest
 * thing is to put it in front of a person rather than retry it forever behind a
 * screen nobody looks at.
 */
export const STALL_AFTER = 8;

/**
 * The device's write path.
 *
 * <b>Every write goes through here, online or not.</b> There is no "offline
 * mode" branch in any screen, and that is the single most load-bearing decision
 * in this folder. A queue that is only reached when `navigator.onLine` is false
 * is a queue that has never been exercised on the happy path, and a confirmation
 * that goes straight to `fetch` is lost when the app is killed between the tap
 * and the response — which is exactly the dead-battery case the durability is
 * for. Writing to IndexedDB first costs about a millisecond and makes "the
 * command survives" structural rather than conditional.
 */
export class Outbox {
  #store: OutboxStore;
  #send: Send;
  #owner: Owner | null = null;
  #listeners = new Set<() => void>();
  #snapshot: OutboxSnapshot = {
    commands: [],
    draining: false,
    lastDrain: null,
    reachable: null,
  };
  #draining: Promise<void> | null = null;
  /** Set while a drain is running and something else asks for one. */
  #rerun = false;

  constructor(store: OutboxStore, send: Send) {
    this.#store = store;
    this.#send = send;
  }

  get store(): OutboxStore {
    return this.#store;
  }

  subscribe = (listener: () => void): (() => void) => {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  };

  getSnapshot = (): OutboxSnapshot => this.#snapshot;

  /**
   * Who is signed in, and therefore whose commands may be sent.
   *
   * Passing `null` — a sign-out — does not empty the queue. The commands stay,
   * owned by the operator who made them, and wait for that operator to come
   * back. See `pendingFor`.
   */
  async setOwner(owner: Owner | null): Promise<void> {
    this.#owner = owner;
    await this.refresh();
  }

  /**
   * Put a device that has just started back into a consistent state.
   *
   * Anything left `sending` goes back to `pending`. The device cannot know
   * whether that request reached the server before the power went — and does not
   * need to, because the command carries the same idempotency key it always did.
   * If it landed, the server replays its answer; if it did not, it lands now.
   * <b>A key regenerated here would be the whole bug</b>, which is why the key is
   * the record's own identity and there is nowhere in this method to mint one.
   */
  async recover(): Promise<void> {
    for (const command of await this.#store.all()) {
      if (command.state === 'sending') {
        await this.#store.put({ ...command, state: 'pending' });
      }
    }

    await this.refresh();
  }

  async refresh(): Promise<void> {
    this.#publish({ commands: await this.#store.all() });
  }

  /**
   * Record what the operator did, then try to tell the server about it.
   *
   * Resolves with the command in whatever state it reached: settled if the
   * server took it, refused if the server said no, still pending if there was
   * nobody to ask. The screen shows all three differently, and must never show
   * the third as success.
   */
  async submit(input: SubmitInput): Promise<QueuedCommand> {
    const owner = this.#owner;
    if (!owner) throw new Error('The outbox has no signed-in operator to attribute work to.');

    const command: QueuedCommand = {
      id: input.key,
      seq: await this.#store.nextSeq(),
      kind: input.kind,
      ownerUserId: owner.userId,
      ownerOperatorCode: owner.operatorCode,
      createdAt: new Date().toISOString(),
      request: input.request,
      summary: input.summary,
      state: 'pending',
      attempts: 0,
    };

    const stored = await this.#store.add(command);
    await this.refresh();
    await this.drain();

    return (await this.#store.get(stored.id)) ?? stored;
  }

  /**
   * Send what can be sent, oldest first.
   *
   * Serialised rather than parallel, because the order commands were made in is
   * the honest reconstruction of a shift and because two in flight at once would
   * make "stop when the network goes" mean two different things.
   */
  drain(): Promise<void> {
    if (this.#draining) {
      // A drain asked for while one is running is not dropped — the running one
      // may already have walked past the command that prompted it.
      this.#rerun = true;
      return this.#draining;
    }

    this.#draining = this.#run().finally(() => {
      this.#draining = null;
    });

    return this.#draining;
  }

  async #run(): Promise<void> {
    do {
      this.#rerun = false;
      this.#publish({ draining: true });

      let sent = 0;
      let refused = 0;

      for (const command of await this.#store.all()) {
        if (command.state !== 'pending') continue;

        // Somebody else's work. It is not ours to send: the ledger records the
        // credential that posted a movement, so replaying OP001's putaway while
        // OP002 is signed in writes a name into an append-only record that never
        // happened. It waits.
        if (command.ownerUserId !== this.#owner?.userId) continue;

        const attempt = { ...command, state: 'sending' as const, lastAttemptAt: new Date().toISOString() };
        await this.#store.put(attempt);
        await this.refresh();

        const outcome = await this.#send(attempt);

        // A refusal is the warehouse talking, so it counts as reached. Only "no
        // answer" and "the server is unwell" mean it cannot hear us.
        this.#publish({
          reachable: outcome.kind !== 'unreachable' && outcome.kind !== 'unwell',
        });

        if (outcome.kind === 'accepted') {
          await this.#store.put({ ...attempt, state: 'settled', result: outcome.result });
          sent += 1;
          await this.#prune();
          await this.refresh();
          continue;
        }

        if (outcome.kind === 'refused') {
          await this.#store.put({
            ...attempt,
            state: 'refused',
            attempts: attempt.attempts + 1,
            refusal: { code: outcome.code, status: outcome.status, at: new Date().toISOString() },
          });
          refused += 1;
          await this.refresh();
          // Deliberately keeps going. The refusal is terminal for this command
          // and says nothing about the next one — the warehouse having taken a
          // job off somebody does not make the pallet behind it unbookable.
          continue;
        }

        if (outcome.kind === 'unauthenticated') {
          // Nothing behind this will fare any better. Put it back and stop.
          await this.#store.put({ ...attempt, state: 'pending' });
          await this.refresh();
          break;
        }

        const attempts = attempt.attempts + 1;
        const stalled = outcome.kind === 'unwell' && attempts >= STALL_AFTER;

        await this.#store.put({
          ...attempt,
          state: stalled ? 'stalled' : 'pending',
          attempts,
        });
        await this.refresh();

        // A stalled command steps aside so the rest of the shift can go; a
        // network that is simply not there stops the drain, because the command
        // after this one is going nowhere either.
        if (stalled) continue;
        break;
      }

      this.#publish({ draining: false, lastDrain: sent + refused > 0 ? { sent, refused } : null });
    } while (this.#rerun);
  }

  /**
   * The operator has seen a refusal and taken it on.
   *
   * Only a refused or a stalled command can be acknowledged. A pending one
   * cannot be cleared from the handheld at all, and that is not an oversight: it
   * is a record of a pallet somebody moved, and letting an operator make it go
   * away turns a queue into a way of losing stock quietly.
   */
  async acknowledge(id: string): Promise<void> {
    const command = await this.#store.get(id);
    if (!command) return;
    if (command.state !== 'refused' && command.state !== 'stalled') return;

    await this.#store.remove(id);
    await this.refresh();
  }

  /**
   * The operator asks for a stalled command to be tried again.
   *
   * A stall means "stop holding the queue up", never "give up": the command is
   * still a record of work somebody did, and the automatic retry stopping is
   * what puts it in front of a person. Without this there is nothing that
   * person can do with it — `drain` only walks pending commands, so the one
   * button on the card would be the one that deletes it, and an API that was
   * down for four minutes would cost the warehouse a movement it never heard
   * about. Found by stopping the API for long enough that `STALL_AFTER` was
   * reached, which is a shorter outage than a shift will meet.
   *
   * The attempt count goes back to zero because the count is a measure of one
   * outage, not of the command.
   */
  async retry(id: string): Promise<void> {
    const command = await this.#store.get(id);
    if (!command || command.state !== 'stalled') return;

    await this.#store.put({ ...command, state: 'pending', attempts: 0 });
    await this.refresh();
    await this.drain();
  }

  /** Clears the "3 sent" line once the shell has shown it. */
  clearLastDrain(): void {
    this.#publish({ lastDrain: null });
  }

  /**
   * What a *read* learned about whether the warehouse is there.
   *
   * The drain finds out on its own, but a device with an empty queue never
   * drains — and it is exactly the device whose operator is about to press "next
   * job". So the screens report what their fetches saw, and the badge is right
   * either way round.
   */
  noteReachable(reachable: boolean): void {
    if (this.#snapshot.reachable === reachable) return;
    this.#publish({ reachable });
  }

  async #prune(): Promise<void> {
    const settled = (await this.#store.all()).filter((c) => c.state === 'settled');
    for (const command of settled.slice(0, Math.max(0, settled.length - SETTLED_KEPT))) {
      await this.#store.remove(command.id);
    }
  }

  #publish(patch: Partial<OutboxSnapshot>): void {
    this.#snapshot = { ...this.#snapshot, ...patch };
    for (const listener of this.#listeners) listener();
  }
}

/** Commands still to be sent, whoever they belong to. */
export function unsent(commands: QueuedCommand[]): QueuedCommand[] {
  return commands.filter((c) => c.state === 'pending' || c.state === 'sending');
}

/** Commands the operator has to do something about. */
export function needsAttention(commands: QueuedCommand[]): QueuedCommand[] {
  return commands.filter((c) => c.state === 'refused' || c.state === 'stalled');
}

/** What is waiting for one operator to come back and send it. */
export function pendingFor(commands: QueuedCommand[], userId: string | null): QueuedCommand[] {
  return unsent(commands).filter((c) => (userId === null ? true : c.ownerUserId === userId));
}

/** What is waiting for somebody who is not signed in. */
export function strandedFrom(commands: QueuedCommand[], userId: string | null): QueuedCommand[] {
  return unsent(commands).filter((c) => c.ownerUserId !== userId);
}

/**
 * Which cached jobs the device should stop showing.
 *
 * The warehouse's list of what belongs to this operator is the authority, so
 * anything the device is holding that is not on it goes — *unless* the device is
 * holding an unsent command for it, because that entry is its own record of work
 * in flight and is not the warehouse's to withdraw.
 *
 * Written as a selector rather than inline in the screen because the failure it
 * prevents is subtle and worth a test: a cache that is only ever added to shows
 * an operator jobs that were finished twenty minutes ago, which on a handheld is
 * an instruction to move a pallet somebody has already moved.
 */
export function staleTaskIds(
  cached: readonly string[],
  authoritative: readonly string[],
  commands: QueuedCommand[],
): string[] {
  const theirs = new Set(authoritative);
  const inFlight = taskIdsAwaitingConfirmation(commands);

  return cached.filter((id) => !theirs.has(id) && !inFlight.has(id));
}

/** The task ids this device has already confirmed and not yet had accepted. */
export function taskIdsAwaitingConfirmation(commands: QueuedCommand[]): Set<string> {
  const ids = new Set<string>();

  for (const command of commands) {
    if (command.kind !== 'work.complete') continue;
    if (command.state !== 'pending' && command.state !== 'sending') continue;
    ids.add((command.request as { taskId: string }).taskId);
  }

  return ids;
}
