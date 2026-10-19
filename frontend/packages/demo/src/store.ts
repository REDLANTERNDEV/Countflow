import { createSeed, type DemoState } from './data.js';

export const DEMO_DB = 'countflow.demo.v1';
export const IDLE_MS = 60 * 60 * 1000;
export function expireIdleState(state: DemoState, now: number): boolean {
  if (now - state.activityAt < IDLE_MS) return false;
  Object.assign(state, createSeed(now));
  return true;
}
export interface DemoStore {
  /** Run synchronously inside a single read/write transaction, including validation. */
  transact<T>(run: (state: DemoState) => T): Promise<T>;
}

export function createBrowserStore(): DemoStore {
  let opened: Promise<IDBDatabase> | undefined;
  function database() {
    return opened ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open(DEMO_DB, 1);
      request.onupgradeneeded = () => request.result.createObjectStore('state');
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
    });
  }
  return {
    async transact<T>(run: (state: DemoState) => T): Promise<T> {
      const db = await database();
      return new Promise<T>((resolve, reject) => {
        const tx = db.transaction('state', 'readwrite');
        const records = tx.objectStore('state');
        const read = records.get('current');
        let result: T;
        let failure: unknown;
        read.onsuccess = () => {
          try {
            const state: DemoState = read.result?.version === 1 ? read.result : createSeed();
            result = run(state);
            records.put(state, 'current');
          } catch (error) { failure = error; tx.abort(); }
        };
        tx.oncomplete = () => resolve(result);
        tx.onabort = tx.onerror = () => reject(failure ?? tx.error ?? new Error('Demo storage unavailable'));
      });
    },
  };
}
