import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { LoadState, describeHttpError, failed, loading, ready } from '../../api/load-state';
import { WorkflowDefinitionDetail, WorkflowStep } from '../../api/models';
import { WorkflowService } from '../../api/workflow.service';

/**
 * One workflow definition, rendered as the shape it declares.
 *
 * The view an operator opens *before* a run goes wrong, so its job is to answer
 * what a workflow does — which steps, in what order, and where it branches.
 *
 * **Nested lists, not a canvas.** The shape is a tree of nested sequences and
 * that is exactly what nested lists express. An ordered list is navigable by a
 * screen reader out of the box where an SVG canvas is not without substantial
 * extra work (ADR-0016), and a graph library would be a large dependency for
 * something HTML already does (ADR-0010).
 *
 * **No run overlay.** Showing which path an instance actually took needs
 * branch-aware execution history, which is #164.
 */
@Component({
  selector: 'app-workflow-detail',
  imports: [NgTemplateOutlet],
  templateUrl: './workflow-detail.html',
  styleUrl: './workflow-detail.css',
})
export class WorkflowDetail implements OnInit {
  private readonly workflows = inject(WorkflowService);

  /** Bound from the route parameter. */
  readonly definitionId = input.required<string>();

  protected readonly state = signal<LoadState<WorkflowDefinitionDetail>>(loading());

  protected readonly definition = computed(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.data : null;
  });

  protected readonly errorMessage = computed(() => {
    const state = this.state();

    return state.kind === 'error' ? state.message : '';
  });

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.state.set(loading());

    this.workflows.get(this.definitionId()).subscribe({
      next: (definition) => this.state.set(ready(definition)),
      error: (error: unknown) => this.state.set(failed(describeHttpError(error))),
    });
  }

  /**
   * Narrows the recursive template's context, which `let-` bindings cannot.
   *
   * `ng-template` context is `any`, so without this the whole nested body would
   * go unchecked — and the shape is the one thing this view is for. The
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
}
