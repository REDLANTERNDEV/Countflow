import type { CachedTask, QueuedCommand } from './types.js';

/**
 * Where queued commands live between a scan and the server hearing about it.
 *
 * An interface rather than a direct IndexedDB call from the driver, for one
 * reason: the driver's behaviour under refusal, under a dead network and across
 * a restart is the part worth testing, and it is testable against an in-memory
 * implementation of six methods. The IndexedDB implementation below is then a
 * small enough thing to verify by using the product with the network off, which
 * is the only way it can honestly be verified anyway.
 */
export interface OutboxStore {
  /** Every command, in `seq` order. */
  all(): Promise<QueuedCommand[]>;
  get(id: string): Promise<QueuedCommand | undefined>;
  /** Insert, or leave an existing record alone. Enqueueing the same key twice is not a second command. */
  add(command: QueuedCommand): Promise<QueuedCommand>;
  put(command: QueuedCommand): Promise<void>;
  remove(id: string): Promise<void>;
  /** The next `seq`. Monotonic across restarts, which is what keeps replay in the order the work happened. */
  nextSeq(): Promise<number>;

  cacheTask(task: CachedTask): Promise<void>;
  readTask(id: string): Promise<CachedTask | undefined>;
  /** Everything the device has cached for one operator, newest first. */
  readTasks(ownerUserId: string): Promise<CachedTask[]>;
  forgetTask(id: string): Promise<void>;
  clearTasks(): Promise<void>;
}

const DB_NAME = 'countflow.handheld';
const DB_VERSION = 1;
const COMMANDS = 'commands';
const TASKS = 'tasks';

/**
 * How many settled commands the device keeps.
 *
 * Not zero, because "did my scans go through?" is a question operators actually
 * ask and a receipt book is the cheapest possible answer. Not unbounded, because
 * a device on a fortnight's shift pattern would accumulate thousands of rows
 * nobody will ever read.
 */
export const SETTLED_KEPT = 20;

/** How many task details the device keeps for offline work. */
export const TASKS_KEPT = 50;

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error('IndexedDB request failed'));
  });
}

function commit(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error ?? new Error('IndexedDB transaction failed'));
    tx.onabort = () => reject(tx.error ?? new Error('IndexedDB transaction aborted'));
  });
}

export function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(DB_NAME, DB_VERSION);

    open.onupgradeneeded = () => {
      const db = open.result;

      if (!db.objectStoreNames.contains(COMMANDS)) {
        // Keyed on the command's id, which *is* its idempotency key. A store
        // that cannot hold two records for one key cannot hold two commands for
        // one intent, which is the invariant the whole feature rests on.
        const commands = db.createObjectStore(COMMANDS, { keyPath: 'id' });
        commands.createIndex('seq', 'seq', { unique: true });
      }

      if (!db.objectStoreNames.contains(TASKS)) {
        db.createObjectStore(TASKS, { keyPath: 'id' });
      }
    };

    open.onsuccess = () => resolve(open.result);
    open.onerror = () => reject(open.error ?? new Error('IndexedDB could not be opened'));
  });
}

/**
 * The durable store.
 *
 * IndexedDB directly rather than through Dexie, and the reason is in ADR 0003:
 * the handheld's production bundle is 132.3 kB gzipped and cold-starts on cheap
 * scanner hardware in about a second. One object store with one index does not
 * justify a wrapper costing more than this whole feature did. The
 * callback-to-promise plumbing is the twenty lines above and it is the whole of
 * what Dexie would have bought here.
 */
export function createIndexedDbStore(db: IDBDatabase): OutboxStore {
  async function read<T>(store: string, run: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    const tx = db.transaction(store, 'readonly');
    const result = await request(run(tx.objectStore(store)));
    await commit(tx);
    return result;
  }

  async function write<T>(store: string, run: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    const tx = db.transaction(store, 'readwrite');
    const result = await request(run(tx.objectStore(store)));
    await commit(tx);
    return result;
  }

  return {
    async all() {
      const rows = await read<QueuedCommand[]>(COMMANDS, (s) => s.getAll() as IDBRequest<QueuedCommand[]>);
      return rows.sort((a, b) => a.seq - b.seq);
    },

    get(id) {
      return read<QueuedCommand | undefined>(
        COMMANDS,
        (s) => s.get(id) as IDBRequest<QueuedCommand | undefined>,
      );
    },

    async add(command) {
      const tx = db.transaction(COMMANDS, 'readwrite');
      const store = tx.objectStore(COMMANDS);

      // `add` rather than `put`, and the existing record wins. Two enqueues under
      // one key are one intent submitted twice — a double-tap on a scanner with a
      // sticky screen — and the second must not overwrite an attempt count or a
      // refusal the first one has already collected.
      const existing = await request<QueuedCommand | undefined>(
        store.get(command.id) as IDBRequest<QueuedCommand | undefined>,
      );

      if (existing) {
        await commit(tx);
        return existing;
      }

      await request(store.add(command));
      await commit(tx);
      return command;
    },

    async put(command) {
      await write(COMMANDS, (s) => s.put(command));
    },

    async remove(id) {
      await write(COMMANDS, (s) => s.delete(id));
    },

    async nextSeq() {
      // The highest seq ever issued, not the count: a queue that has drained back
      // to empty must not restart its numbering, or a new command sorts ahead of
      // one still waiting.
      const tx = db.transaction(COMMANDS, 'readonly');
      const cursor = await request(
        tx.objectStore(COMMANDS).index('seq').openCursor(null, 'prev'),
      );
      const highest = cursor?.value ? (cursor.value as QueuedCommand).seq : 0;
      await commit(tx);
      return highest + 1;
    },

    async cacheTask(task) {
      await write(TASKS, (s) => s.put(task));

      const rows = await read<CachedTask[]>(TASKS, (s) => s.getAll() as IDBRequest<CachedTask[]>);
      if (rows.length <= TASKS_KEPT) return;

      const stale = rows
        .sort((a, b) => b.fetchedAt.localeCompare(a.fetchedAt))
        .slice(TASKS_KEPT);

      const tx = db.transaction(TASKS, 'readwrite');
      for (const row of stale) tx.objectStore(TASKS).delete(row.id);
      await commit(tx);
    },

    readTask(id) {
      return read<CachedTask | undefined>(TASKS, (s) => s.get(id) as IDBRequest<CachedTask | undefined>);
    },

    async readTasks(ownerUserId) {
      const rows = await read<CachedTask[]>(TASKS, (s) => s.getAll() as IDBRequest<CachedTask[]>);
      return rows
        .filter((row) => row.ownerUserId === ownerUserId)
        .sort((a, b) => b.fetchedAt.localeCompare(a.fetchedAt));
    },

    async forgetTask(id) {
      await write(TASKS, (s) => s.delete(id));
    },

    async clearTasks() {
      await write(TASKS, (s) => s.clear());
    },
  };
}

/**
 * The same store, in memory.
 *
 * Used by the driver's tests, and as the fallback when IndexedDB is unavailable
 * — a device in a private window, or a browser that has evicted the origin's
 * storage. The fallback is deliberately *not* silent: the app reports that the
 * queue is not durable, because an operator who believes their work is safe
 * across a battery change and is wrong is worse off than one who knows to stay
 * in signal.
 */
export function createMemoryStore(): OutboxStore {
  const commands = new Map<string, QueuedCommand>();
  const tasks = new Map<string, CachedTask>();
  let issued = 0;

  return {
    async all() {
      return [...commands.values()].sort((a, b) => a.seq - b.seq);
    },
    async get(id) {
      return commands.get(id);
    },
    async add(command) {
      const existing = commands.get(command.id);
      if (existing) return existing;
      commands.set(command.id, command);
      return command;
    },
    async put(command) {
      commands.set(command.id, command);
    },
    async remove(id) {
      commands.delete(id);
    },
    async nextSeq() {
      issued = Math.max(issued, ...[...commands.values()].map((c) => c.seq), 0);
      return ++issued;
    },
    async cacheTask(task) {
      tasks.set(task.id, task);
    },
    async readTask(id) {
      return tasks.get(id);
    },
    async readTasks(ownerUserId) {
      return [...tasks.values()]
        .filter((row) => row.ownerUserId === ownerUserId)
        .sort((a, b) => b.fetchedAt.localeCompare(a.fetchedAt));
    },
    async forgetTask(id) {
      tasks.delete(id);
    },
    async clearTasks() {
      tasks.clear();
    },
  };
}
