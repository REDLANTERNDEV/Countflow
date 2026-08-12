import type { components } from '@countflow/api-client';
import { staleTaskIds, taskIdsAwaitingConfirmation } from './lib/outbox/outbox.js';
import { outbox } from './outbox.js';

export type TaskDetail = components['schemas']['WorkTaskDetailResponse'];

/**
 * What the device knows about the jobs it is holding.
 *
 * A task can be worked out of signal only if the device already has it. That is
 * the honest limit and it falls straight out of ADR 0008: a claim is a server
 * decision, so a handheld may keep working what it was given and may never help
 * itself to more. Everything below is therefore a cache of reads, never a source
 * of them — nothing here decides anything.
 *
 * The staleness is real and is shown. A claim held while the device was away may
 * have been released by a supervisor, and the operator finds out when the
 * confirmation is refused with `work.task_not_yours`. There is no way to know
 * sooner without a network, and pretending otherwise would be worse.
 */
export async function rememberTask(detail: TaskDetail, ownerUserId: string): Promise<void> {
  // The queue listing carries no decision trace — only `GET /work/tasks/{id}`
  // does — so a refresh of the list must not overwrite a trace the device has
  // already been given. "Why here?" at a rack with no signal is exactly the
  // moment the explanation is worth most, and losing it to a background refresh
  // would be an odd way to keep ADR 0004's promise.
  const existing = (await outbox.store.readTask(detail.task.id))?.detail as TaskDetail | undefined;

  const merged: TaskDetail =
    detail.decision === null && existing?.decision
      ? { ...detail, decision: existing.decision }
      : detail;

  await outbox.store.cacheTask({
    id: detail.task.id,
    fetchedAt: new Date().toISOString(),
    ownerUserId,
    detail: merged,
  });
}

export async function recallTask(
  id: string,
): Promise<{ detail: TaskDetail; fetchedAt: string } | null> {
  const cached = await outbox.store.readTask(id);
  if (!cached) return null;

  return { detail: cached.detail as TaskDetail, fetchedAt: cached.fetchedAt };
}

/**
 * Bring the device's idea of its jobs back into line with the warehouse's.
 *
 * <b>The cache has to be pruned, not merely added to, and finding that out cost a
 * defect.</b> A cache that only ever grows showed an operator two putaways as
 * "jobs on this device" that the warehouse had finished with twenty minutes
 * earlier — the entries were written when the tasks were read, and nothing ever
 * took them out again. A stale job on a handheld is not a cosmetic problem: it is
 * an instruction to go and move a pallet somebody has already moved.
 *
 * `GET /work/tasks?mine=true&status=Assigned` is the authority, so what survives
 * is what it lists — plus anything with a command still unsent, because that entry
 * is the device's own record of work in flight and is not the warehouse's to
 * withdraw.
 *
 * The consequence is worth stating: **a job has to be claimed to be workable
 * offline.** A `Ready` task an operator merely looked at is not theirs, and is
 * pruned. That is the same line ADR 0008 draws everywhere else — the claim is a
 * server decision, so a device may finish what it was given and may never help
 * itself to more.
 */
export async function reconcileTasks(
  authoritative: TaskDetail['task'][],
  ownerUserId: string,
): Promise<void> {
  for (const task of authoritative) {
    await rememberTask({ task, decision: null }, ownerUserId);
  }

  const cached = await outbox.store.readTasks(ownerUserId);

  const stale = staleTaskIds(
    cached.map((row) => row.id),
    authoritative.map((task) => task.id),
    outbox.getSnapshot().commands,
  );

  for (const id of stale) await outbox.store.forgetTask(id);
}

/**
 * The jobs this operator is holding, as the device last understood it.
 *
 * A task with a confirmation already queued is left out: it has been done, and
 * offering it again is how an operator confirms one job twice under two keys and
 * collects a `work.task_already_completed` they caused themselves.
 */
export async function recallMyTasks(ownerUserId: string): Promise<TaskDetail[]> {
  const cached = await outbox.store.readTasks(ownerUserId);
  const queued = taskIdsAwaitingConfirmation(outbox.getSnapshot().commands);

  return cached
    .map((row) => row.detail as TaskDetail)
    .filter((detail) => detail.task.status === 'Assigned' || detail.task.status === 'Ready')
    .filter((detail) => !queued.has(detail.task.id));
}
