interface Detail {
  task: { decisionTraceId?: string | null };
  decision: { id: string } | null;
}

/** Preserve a cached explanation when a queue refresh omits the full trace. */
export function mergeTaskDecision<T extends Detail>(incoming: T, cached: T | undefined): T {
  return incoming.decision === null && cached?.decision && cached.decision.id === incoming.task.decisionTraceId
    ? { ...incoming, decision: cached.decision }
    : incoming;
}
