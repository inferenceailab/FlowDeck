import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { InstanceService } from '../../api/instance.service';
import { LoadState, describeHttpError, failed, loading, ready } from '../../api/load-state';
import { Instance, StepHistoryEntry, isTerminal } from '../../api/models';
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

  /** Whether the confirmation prompt is showing. */
  protected readonly confirming = signal(false);

  /** Whether a cancel request is in flight. */
  protected readonly cancelling = signal(false);

  /** A failed cancel, shown without discarding the loaded instance. */
  protected readonly cancelError = signal<string | null>(null);

  /**
   * Whether this instance can still be cancelled.
   *
   * Mirrors the engine's rule (ADR-0008) so the UI does not offer an action the
   * API will refuse. The API remains the authority - disabling a button is a
   * courtesy, not enforcement, and a stale view can still produce a 409.
   */
  protected readonly canCancel = computed(() => {
    const instance = this.instance();

    return instance !== null && !isTerminal(instance.status);
  });

  ngOnInit(): void {
    this.load();
  }

  /**
   * Cancels the instance after confirmation.
   *
   * Two steps, not one. Cancelling is irreversible - terminal states are final
   * - and a misclick that silently stops a long-running workflow is expensive.
   */
  protected confirmCancel(): void {
    this.cancelError.set(null);
    this.confirming.set(true);
  }

  protected dismissCancel(): void {
    this.confirming.set(false);
  }

  protected cancel(): void {
    this.cancelling.set(true);
    this.cancelError.set(null);

    this.instances.cancel(this.instanceId()).subscribe({
      next: () => {
        this.cancelling.set(false);
        this.confirming.set(false);

        // Reloaded rather than patched from the response. Cancelling ends the
        // instance, and the history is what an operator looks at next.
        this.load();
      },
      error: (error: unknown) => {
        this.cancelling.set(false);
        this.confirming.set(false);

        // Kept beside the instance rather than replacing the view. A failed
        // cancel does not mean the instance could not be loaded, and blanking
        // the page would lose the context the operator was acting on.
        this.cancelError.set(describeHttpError(error));
      },
    });
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

  /**
   * Whether this entry is a retry rather than a first execution.
   *
   * `attempt` is generated as `number | string` because the served OpenAPI
   * document declares int32 as either. Coerced here rather than compared
   * loosely in the template, where `'2' > 1` would be a string comparison
   * quietly doing the right thing for the wrong reason.
   */
  protected isRetry(entry: StepHistoryEntry): boolean {
    return Number(entry.attempt) > 1;
  }
}
