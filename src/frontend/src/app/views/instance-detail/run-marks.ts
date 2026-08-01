import { Instance, StepHistoryEntry, WorkflowBranch, WorkflowStep, isTerminal } from '../../api/models';
import { StepMark } from '../../components/workflow-shape/workflow-shape';

/**
 * How the engine names a compensating action in history (ADR-0021).
 *
 * A prefix rather than a separate field, so the contract is one string both
 * sides agree on. Declared once here rather than inline, because a typo in a
 * `startsWith` would silently classify every rollback as an ordinary step.
 */
export const ROLLBACK_PREFIX = 'compensate:';

/** Every step in a branch, including those nested in branches of its own. */
const stepsIn = (branch: WorkflowBranch): WorkflowStep[] =>
  branch.steps.flatMap((step) => [step, ...step.branches.flatMap(stepsIn)]);

/**
 * What a run did to each step of the shape it is running, by step name.
 *
 * Keyed by name because names are unique across the whole graph (#162), which
 * the builder enforces precisely so that a name identifies a node. History
 * records names rather than positions for the same reason: an index means
 * something only relative to a sequence, and a step inside a branch is not in
 * the top-level one.
 *
 * A step the run has not reached is absent rather than carrying a "not run"
 * mark. Absence is the common case, and badging it would bury the four states
 * that matter under one that does not.
 */
export function runMarks(
  steps: readonly WorkflowStep[],
  history: readonly StepHistoryEntry[],
  instance: Instance,
): ReadonlyMap<string, StepMark> {
  const forward = new Map<string, StepHistoryEntry[]>();
  const rollback = new Map<string, StepHistoryEntry>();

  for (const entry of history) {
    if (entry.stepName.startsWith(ROLLBACK_PREFIX)) {
      rollback.set(entry.stepName.slice(ROLLBACK_PREFIX.length), entry);
    } else {
      forward.set(entry.stepName, [...(forward.get(entry.stepName) ?? []), entry]);
    }
  }

  const marks = new Map<string, StepMark>();

  const markOf = (name: string): StepMark | null => {
    const attempts = forward.get(name) ?? [];
    const succeeded = attempts.some((entry) => entry.status === 'Success');

    // The failure point first, and only where no attempt succeeded - a step
    // that failed once and passed on retry ran, and reporting it as the place
    // the workflow broke would send an operator to the wrong step.
    //
    // It outranks the undo even though a step that exhausted its retries is
    // compensated too (ADR-0021). Where the run broke is what this view exists
    // to answer, and the timeline below still carries the rollback.
    if (!succeeded && attempts.some((entry) => entry.status === 'Failed')) {
      return 'failed';
    }

    const undo = rollback.get(name);

    if (undo) {
      return undo.status === 'Failed' ? 'undo-failed' : 'undone';
    }

    if (succeeded) {
      return 'ran';
    }

    // Only while the instance can still move. A terminal instance keeps
    // CurrentStepName pointing at where it stopped, which is a gravestone
    // rather than a position - the same distinction the engine draws.
    return instance.currentStepName === name && !isTerminal(instance.status) ? 'running' : null;
  };

  const mark = (list: readonly WorkflowStep[]): void => {
    for (const step of list) {
      const found = markOf(step.name);

      if (found !== null) {
        marks.set(step.name, found);
      }

      for (const branch of step.branches) {
        mark(branch.steps);
      }
    }
  };

  mark(steps);

  const touched = (branch: WorkflowBranch): boolean =>
    stepsIn(branch).some((step) => forward.has(step.name) || rollback.has(step.name));

  /**
   * Marks the arms a choice did not take.
   *
   * Only a choice. "Not reached yet" and "on a path we skipped" look identical
   * for most of a run, and the one case that is provable is this one: a choice
   * takes exactly one branch (ADR-0024), so a sibling having history settles
   * it. A fork runs every arm, so the same inference there would report
   * finished work as never attempted.
   */
  const skip = (list: readonly WorkflowStep[]): void => {
    for (const step of list) {
      const isChoice = step.branches.length > 0 && !step.branches[0].isParallel;

      if (isChoice && step.branches.some(touched)) {
        for (const branch of step.branches.filter((candidate) => !touched(candidate))) {
          for (const inside of stepsIn(branch)) {
            // Never over an existing mark. Nothing should have one here, and a
            // silent overwrite is how a run that did happen would come to read
            // as one that never started.
            if (!marks.has(inside.name)) {
              marks.set(inside.name, 'not-taken');
            }
          }
        }
      }

      for (const branch of step.branches) {
        skip(branch.steps);
      }
    }
  };

  skip(steps);

  return marks;
}
