import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { InstanceService } from '../../api/instance.service';
import { LoadState, describeHttpError, failed, loading, ready } from '../../api/load-state';
import { Instance, InstancePage } from '../../api/models';
import { StatusBadge } from '../../components/status-badge/status-badge';

/**
 * Lists workflow instances.
 *
 * State lives in signals and is fetched through {@link InstanceService}
 * (ADR-0018).
 */
@Component({
  selector: 'app-instance-list',
  imports: [DatePipe, RouterLink, StatusBadge],
  templateUrl: './instance-list.html',
  styleUrl: './instance-list.css',
})
export class InstanceList implements OnInit, OnDestroy {
  /**
   * How often the list refreshes.
   *
   * Workflows run for seconds to days, so sub-second latency buys nothing and
   * costs a request per second per open tab. Five seconds is fast enough that
   * an operator watching a run sees it progress, and slow enough to be
   * unremarkable load.
   */
  static readonly RefreshIntervalMs = 5000;

  private readonly instances = inject(InstanceService);

  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly state = signal<LoadState<InstancePage>>(loading());

  /** Whether a background refresh is currently in flight. */
  protected readonly refreshing = signal(false);

  /**
   * The loaded page, or an empty one.
   *
   * Angular templates cannot narrow a discriminated union through `@switch`, so
   * the narrowing happens here where the compiler can check it. The fallback is
   * never rendered - the template only reads this inside the `ready` branch -
   * but returning an empty page rather than `null` keeps the template free of
   * optional chaining that would hide a genuine bug.
   */
  protected readonly page = computed<InstancePage>(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.data : { items: [], total: 0, page: 1, pageSize: 0 };
  });

  /** The failure message, or empty when there is no failure. */
  protected readonly errorMessage = computed(() => {
    const state = this.state();

    return state.kind === 'error' ? state.message : '';
  });

  ngOnInit(): void {
    this.load();

    this.timer = setInterval(() => this.refresh(), InstanceList.RefreshIntervalMs);
  }

  ngOnDestroy(): void {
    // Without this the timer outlives the view and keeps requesting for a page
    // nobody is looking at, forever.
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  /** Fetches the first page, replacing whatever is shown. */
  protected load(): void {
    this.state.set(loading());

    this.instances.list().subscribe({
      next: (page) => this.state.set(ready(page)),

      // The error is turned into a message here rather than stored raw, so the
      // template never has to know about HTTP.
      error: (error: unknown) => this.state.set(failed(describeHttpError(error))),
    });
  }

  /**
   * Refreshes in the background, without disturbing what is on screen.
   *
   * Deliberately not {@link load}. Setting the loading state on every tick
   * would replace the table with a spinner every few seconds, so a row an
   * operator was reading would vanish under them.
   *
   * A failed refresh is also swallowed: a single dropped poll is not worth
   * discarding good data for. The next tick retries, and a persistent outage
   * surfaces when the operator acts on something stale and gets an error from
   * the API.
   */
  protected refresh(): void {
    if (this.refreshing()) {
      // A slow response must not stack requests behind it.
      return;
    }

    this.refreshing.set(true);

    this.instances.list().subscribe({
      next: (page) => {
        this.refreshing.set(false);
        this.state.set(ready(page));
      },
      error: () => this.refreshing.set(false),
    });
  }

  /**
   * Shortens an instance id for display.
   *
   * A full GUID in every row crowds out the columns an operator actually scans.
   * The full value stays in the cell's title attribute, so nothing is lost.
   */
  protected shortId(instance: Instance): string {
    return instance.id.slice(0, 8);
  }
}
