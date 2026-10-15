interface TourStop { id: string; pickTourId?: string | null; tourSequence?: number | null; status: string }
interface Completion { kind: string; state: string; request: unknown }

export function tourStops<T extends TourStop>(tasks: T[], tourId: string): T[] {
  return tasks.filter(task => task.pickTourId === tourId && task.status === 'Assigned')
    .sort((a, b) => (a.tourSequence ?? 0) - (b.tourSequence ?? 0));
}

export function hasUnsentTourWork(ids: string[], commands: Completion[]): boolean {
  return commands.some(command => command.kind === 'work.complete' && command.state !== 'settled'
    && ids.includes((command.request as { taskId: string }).taskId));
}
