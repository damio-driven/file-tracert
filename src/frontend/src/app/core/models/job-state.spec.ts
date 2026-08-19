import { describe, expect, it } from 'vitest';

import { JobState } from './catalog.models';
import {
  JOB_STATE_KIND, isActiveJobState, isQueuedJobState, isTerminalJobState,
} from './job-state';

/**
 * K8 — the classification used to exist twice (typed in `queue.ts`, `Set<string>` in
 * `queue.store.ts`), and the untyped copy could drift by a typo without the compiler
 * noticing. The Record is exhaustive by construction; these tests pin the meanings so a
 * future edit has to state what it is changing.
 */
describe('job state classification', () => {
  it('classifies every state exactly once', () => {
    const states = Object.keys(JOB_STATE_KIND) as JobState[];

    expect(states).toHaveLength(9);
    expect(new Set(states).size).toBe(9);
    for (const state of states) {
      const kinds = [
        isQueuedJobState(state), isActiveJobState(state), isTerminalJobState(state),
      ].filter(Boolean);
      // Blocked belongs to none of the three predicates: it is its own kind.
      expect(kinds.length).toBeLessThanOrEqual(1);
    }
  });

  it('only the states that move bytes are active', () => {
    expect((Object.keys(JOB_STATE_KIND) as JobState[]).filter(isActiveJobState))
      .toEqual(['Copying', 'Verifying', 'DeletingSource']);
  });

  it('Blocked is not terminal: it is waiting, and recoverable', () => {
    expect(isTerminalJobState('Blocked')).toBe(false);
    expect((Object.keys(JOB_STATE_KIND) as JobState[]).filter(isTerminalJobState))
      .toEqual(['Completed', 'Failed', 'Cancelled']);
  });

  it('queued means waiting for its turn, not parked and not started', () => {
    expect((Object.keys(JOB_STATE_KIND) as JobState[]).filter(isQueuedJobState))
      .toEqual(['Pending', 'SpaceReserved']);
  });
});
