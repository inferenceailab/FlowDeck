import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { LoadState, describeHttpError, failed, loading, ready } from '../../api/load-state';
import { WorkflowDefinitionDetail } from '../../api/models';
import { WorkflowService } from '../../api/workflow.service';
import { WorkflowShape } from '../../components/workflow-shape/workflow-shape';

/**
 * One workflow definition, rendered as the shape it declares.
 *
 * The view an operator opens *before* a run goes wrong, so its job is to answer
 * what a workflow does — which steps, in what order, and where it branches.
 *
 * The drawing itself is {@link WorkflowShape}, shared with the instance view.
 * This one passes no marks, because a definition has not run: what a *run* did
 * to the shape is the instance view's question (#181).
 */
@Component({
  selector: 'app-workflow-detail',
  imports: [WorkflowShape],
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
}
