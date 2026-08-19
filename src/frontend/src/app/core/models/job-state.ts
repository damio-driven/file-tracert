import { JobState } from './catalog.models';

/**
 * What a job state MEANS to the UI, as opposed to what it is called.
 * `blocked` is its own kind, not a flavour of `queued`: the queue screen and the Dashboard
 * both count it separately, and a parked job is the one thing the user may have to act on.
 */
export type JobStateKind = 'queued' | 'active' | 'blocked' | 'terminal';

/**
 * Every job state, classified once (K8).
 *
 * This lives beside the `JobState` union deliberately, and it is a `Record` keyed by that
 * union rather than a pair of hand-written Sets: a Record is exhaustive by construction, so
 * adding a state to `JobState` stops the build until it has been classified here. The two
 * copies this replaces — one in `queue.ts`, one in `queue.store.ts` typed `Set<string>` —
 * could drift, and a typo in the untyped one would have been invisible to the compiler while
 * silently changing which rows the screen called "in corso".
 */
export const JOB_STATE_KIND: Record<JobState, JobStateKind> = {
  Pending: 'queued',
  SpaceReserved: 'queued',
  Copying: 'active',
  Verifying: 'active',
  DeletingSource: 'active',
  Blocked: 'blocked',
  Completed: 'terminal',
  Failed: 'terminal',
  Cancelled: 'terminal',
};

/** The engine is physically moving bytes for this job right now. */
export function isActiveJobState(state: JobState): boolean {
  return JOB_STATE_KIND[state] === 'active';
}

/** The job will never run again. `Blocked` is NOT terminal: it is waiting, and recoverable. */
export function isTerminalJobState(state: JobState): boolean {
  return JOB_STATE_KIND[state] === 'terminal';
}

/** Queued and waiting for its turn — not started, not parked, not finished. */
export function isQueuedJobState(state: JobState): boolean {
  return JOB_STATE_KIND[state] === 'queued';
}
