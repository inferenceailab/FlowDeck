import { describe, expect, it } from 'vitest';
import { INSTANCE_STATUSES, InstanceStatus, isTerminal } from './models';

describe('instance status model', () => {
  it('treats every compensation status as terminal', () => {
    // #120. A Compensated instance that reported non-terminal would show a
    // Cancel button the API refuses, and the operator would be told their
    // action failed on an instance that finished cleanly.
    expect(isTerminal('Compensated')).toBe(true);
    expect(isTerminal('CompensationFailed')).toBe(true);
  });

  it('still treats in-flight statuses as non-terminal', () => {
    expect(isTerminal('Running')).toBe(false);
    expect(isTerminal('Suspended')).toBe(false);
  });

  it('lists every status the engine can report', () => {
    // The filter dropdown is built from this list, so a status missing here is
    // a status an operator cannot filter to - and therefore cannot find.
    //
    // The type-level check in models.ts makes omission a compile error; this
    // asserts the runtime array as well, because a compile-time guarantee that
    // nothing exercises is easy to weaken by accident.
    const expected: InstanceStatus[] = [
      'Running',
      'Suspended',
      'Completed',
      'Failed',
      'Cancelled',
      'Compensated',
      'CompensationFailed',
    ];

    expect([...INSTANCE_STATUSES].sort()).toEqual(expected.sort());
  });
});
