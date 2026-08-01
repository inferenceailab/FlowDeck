import { NgTemplateOutlet } from '@angular/common';
import { Component, input } from '@angular/core';
import { WorkflowBranch, WorkflowStep } from '../../api/models';

/**
 * What a run did to one step, where history proves it.
 *
 * There is deliberately no `not run` member. A step the run has not reached
 * carries no mark at all, the same rule the retry and undo badges follow: every
 * step of a workflow that has barely started would otherwise be badged, which
 * is noise on the common case to serve the rare one.
 */
export type StepMark = 'ran' | 'running' | 'failed' | 'undone' | 'undo-failed' | 'not-taken';

/**
 * A workflow's shape, optionally with a run drawn on it.
 *
 * **Nested lists, not a canvas.** The shape is a tree of nested sequences and
 * that is exactly what nested lists express. An ordered list is navigable by a
 * screen reader out of the box where an SVG canvas is not without substantial
 * extra work (ADR-0016), and a graph library would be a large dependency for
 * something HTML already does (ADR-0010).
 *
 * Shared by the workflow view, which renders a definition nobody has run, and
 * the instance view, which renders one somebody is running. Two copies of a
 * recursive renderer would drift, and the drift would be silent: both would
 * still draw *a* shape.
 */
@Component({
  selector: 'app-workflow-shape',
  imports: [NgTemplateOutlet],
  templateUrl: './workflow-shape.html',
  styleUrl: './workflow-shape.css',
})
export class WorkflowShape {
  /** The steps to draw, in declaration order. */
  readonly steps = input.required<readonly WorkflowStep[]>();

  /**
   * What a run did to each step, by step name, or null for no run at all.
   *
   * Keyed by name because names are unique across the whole graph (#162) — the
   * builder rejects a definition where they are not, precisely so that a name
   * identifies a node.
   */
  readonly marks = input<ReadonlyMap<string, StepMark> | null>(null);

  /**
   * Narrows the recursive template's context, which `let-` bindings cannot.
   *
   * `ng-template` context is `any`, so without this the whole nested body would
   * go unchecked — and the shape is the one thing this component is for. The
   * recursion itself has to be a template rather than the component rendering
   * itself, because a standalone component cannot list itself in `imports`.
   */
  protected asSteps(value: unknown): readonly WorkflowStep[] {
    return value as readonly WorkflowStep[];
  }

  /**
   * Whether a step may run more than once.
   *
   * `maxAttempts` is generated as `number | string` because the served OpenAPI
   * document declares int32 as either. Coerced here rather than compared
   * loosely in the template, where `'3' > 1` would be a string comparison
   * quietly doing the right thing for the wrong reason.
   */
  protected retries(step: WorkflowStep): boolean {
    return Number(step.maxAttempts) > 1;
  }

  /** What the run did to this step, or null where it did nothing yet. */
  protected markOf(step: WorkflowStep): StepMark | null {
    return this.marks()?.get(step.name) ?? null;
  }

  /**
   * Whether the run went the other way at this choice.
   *
   * Derived from the steps rather than passed in: a branch has no identity of
   * its own to key on. Names are unique among a step's own branches and nowhere
   * else — `Fork` labels every fork's arms `branch-1` and `branch-2`, so two
   * forks in one workflow produce the same pair.
   */
  protected notTaken(branch: WorkflowBranch): boolean {
    return this.marks() !== null && this.stepsOf(branch).every((step) => this.markOf(step) === 'not-taken');
  }

  /** Every step inside a branch, including those nested deeper. */
  private stepsOf(branch: WorkflowBranch): WorkflowStep[] {
    return branch.steps.flatMap((step) => [
      step,
      ...step.branches.flatMap((nested) => this.stepsOf(nested)),
    ]);
  }
}
