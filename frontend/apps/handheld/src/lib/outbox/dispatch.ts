import type { CountflowClient } from '@countflow/api-client';
import type { SendOutcome } from './outbox.js';
import type { CompleteCommandRequest, QueuedCommand, ReceiptCommandRequest } from './types.js';

/**
 * The one place a queued command becomes an HTTP request.
 *
 * <b>It calls the published endpoints and nothing else.</b> There is no batch
 * endpoint and no second write path — Inventory has exactly one posting path and
 * this phase does not make it two (ADR 0007). A replayed confirmation is byte
 * for byte the request the handheld would have sent at the rack, including its
 * `Idempotency-Key`; the only difference is what time it is.
 *
 * That is also why the queue could never have been built on a "sync" endpoint
 * that took an array. The server's idempotency guarantee is per command, the
 * refusals are per command, and an array would have needed a per-element result
 * shape that is the queue's problem restated on the wrong side of the wire.
 */
export function createDispatch(api: CountflowClient) {
  return async function send(command: QueuedCommand): Promise<SendOutcome> {
    try {
      const { data, error, response } = await call(api, command);

      if (response.ok && data !== undefined) {
        return { kind: 'accepted', result: data };
      }

      return classify(response.status, error);
    } catch {
      // openapi-fetch does not catch a rejected `fetch`, so everything that never
      // reached a server — no radio, no route, a socket dropped mid-flight —
      // arrives here. None of it is an answer about the command.
      return { kind: 'unreachable' };
    }
  };
}

function call(api: CountflowClient, command: QueuedCommand) {
  if (command.kind === 'work.complete') {
    const request = command.request as CompleteCommandRequest;

    return api.POST('/api/v1/work/tasks/{id}/complete', {
      params: {
        path: { id: request.taskId },
        header: { 'Idempotency-Key': command.id },
      },
      body: {
        toLocationCode: request.toLocationCode,
        ...(request.pickTourId ? { pickTourId: request.pickTourId } : {}),
        ...(request.expectedDecisionTraceId ? { expectedDecisionTraceId: request.expectedDecisionTraceId } : {}),
        ...(request.overrideReason ? { overrideReason: request.overrideReason } : {}),
        ...(request.countedQuantity !== undefined ? { countedQuantity: request.countedQuantity } : {}),
        ...(request.binCountVersion !== undefined ? { binCountVersion: request.binCountVersion } : {}),
        ...(request.binCountClaimId ? { binCountClaimId: request.binCountClaimId } : {}),
        ...(request.observed !== undefined ? { observed: request.observed } : {}),
      },
    });
  }

  const request = command.request as ReceiptCommandRequest;

  return api.POST('/api/v1/inbound/receipts', {
    params: { header: { 'Idempotency-Key': command.id } },
    body: {
      sku: request.sku,
      quantity: request.quantity,
      toLocationCode: request.toLocationCode,
      ...(request.sscc ? { sscc: request.sscc } : {}),
      ...(request.asnReference ? { asnReference: request.asnReference } : {}),
    },
  });
}

/**
 * Which failures are answers about the command, and which are answers about the
 * moment.
 *
 * The line matters more than the codes on either side of it. A refusal is
 * terminal — retrying earns the identical no and burns a shift's battery — while
 * everything on the other side is the command still waiting for its turn. Two
 * statuses look like refusals and are not: **429** is the rate limiter saying
 * "not this second", and a device coming out of a dead zone with fifteen queued
 * commands is precisely what trips it; **408** is a timeout, which is the server
 * declining to answer rather than answering.
 */
export function classify(status: number, error: unknown): SendOutcome {
  if (status === 401) return { kind: 'unauthenticated' };
  if (unanswered(status)) return { kind: 'unwell', status };

  const code = (error as { code?: string } | undefined)?.code;
  return { kind: 'refused', code: code ?? 'unknown', status };
}

/**
 * Whether a failure means nobody answered, as opposed to the answer being no.
 *
 * <b>Exported because the reads need it too, and finding that out cost a defect.</b>
 * The handheld is served by Caddy with the API behind it, so a stopped API does
 * not produce a rejected `fetch` — it produces a **502 from the proxy**. Screens
 * that decided "we must be offline" by catching an exception therefore never
 * decided it, and a reload against a dead API signed the operator out and
 * stranded the queued work on a device nobody could send it from. That is the
 * shape of failure a mocked `fetch` cannot show you, because a mock has no
 * reverse proxy in front of it.
 *
 * `undefined` is the thrown case: no response at all.
 */
export function unanswered(status: number | undefined): boolean {
  return status === undefined || status >= 500 || status === 429 || status === 408;
}
