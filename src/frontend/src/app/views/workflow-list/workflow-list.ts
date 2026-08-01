import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { LoadState, describeHttpError, failed, loading, ready } from '../../api/load-state';
import { WorkflowDefinition } from '../../api/models';
import { WorkflowService } from '../../api/workflow.service';

/**
 * Registered workflow definitions, and what is still running each version.
 *
 * The question this view answers is "what can I retire". Retirement is refused
 * while instances hold a version (ADR-0026), so without this the only way to
 * find out is to attempt the removal and read the error.
 */
@Component({
  selector: 'app-workflow-list',
  imports: [RouterLink],
  templateUrl: './workflow-list.html',
  styleUrl: './workflow-list.css',
})
export class WorkflowList implements OnInit {
  private readonly workflows = inject(WorkflowService);

  protected readonly state = signal<LoadState<readonly WorkflowDefinition[]>>(loading());

  protected readonly definitions = computed(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.data : [];
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

    this.workflows.list().subscribe({
      next: (definitions) => this.state.set(ready(definitions)),
      error: (error: unknown) => this.state.set(failed(describeHttpError(error))),
    });
  }

  /**
   * Whether anything is still running this version.
   *
   * `activeInstances` is generated as `number | string` because the served
   * OpenAPI document declares int32 as either. Coerced here rather than tested
   * loosely in the template, where `'0'` is truthy and would report every idle
   * version as busy — the exact inversion an operator would act on.
   */
  protected inUse(definition: WorkflowDefinition): boolean {
    return this.count(definition) > 0;
  }

  protected count(definition: WorkflowDefinition): number {
    return Number(definition.activeInstances);
  }
}
