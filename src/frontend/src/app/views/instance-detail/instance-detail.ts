import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { InstanceService } from '../../api/instance.service';
import { LoadState, describeHttpError, failed, loading, ready } from '../../api/load-state';
import { Instance, StepHistoryEntry } from '../../api/models';
import { StatusBadge } from '../../components/status-badge/status-badge';

/** An instance together with its execution history. */
interface InstanceDetailData {
  readonly instance: Instance;
  readonly history: readonly StepHistoryEntry[];
}

/**
 * One instance, with the timeline of what actually ran.
 *
 * The view an operator opens when something went wrong, so its job is to answer
 * *where* and *why* - not merely to report that a run failed.
 */
@Component({
  selector: 'app-instance-detail',
  imports: [DatePipe, DecimalPipe, StatusBadge],
  templateUrl: './instance-detail.html',
  styleUrl: './instance-detail.css',
})
export class InstanceDetail implements OnInit {
  private readonly instances = inject(InstanceService);

  /** Bound from the route parameter. */
  readonly instanceId = input.required<string>();

  protected readonly state = signal<LoadState<InstanceDetailData>>(loading());

  protected readonly instance = computed(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.data.instance : null;
  });

  protected readonly history = computed(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.data.history : [];
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

    // Both in one go. Fetching sequentially would show the instance and then
    // pop the timeline in a moment later, which reads as a second load rather
    // than one view arriving.
    forkJoin({
      instance: this.instances.get(this.instanceId()),
      history: this.instances.history(this.instanceId()),
    }).subscribe({
      next: (data) => this.state.set(ready(data)),
      error: (error: unknown) => this.state.set(failed(describeHttpError(error))),
    });
  }

  /**
   * Whether an entry is the point at which the run failed.
   *
   * A step can appear more than once - a re-entered step after a resume - so
   * this matches on the entry's own status rather than on the instance's
   * failed step name, which would mark every attempt.
   */
  protected isFailurePoint(entry: StepHistoryEntry): boolean {
    return entry.status === 'Failed';
  }
}
