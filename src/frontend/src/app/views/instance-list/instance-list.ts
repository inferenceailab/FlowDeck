import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
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
export class InstanceList implements OnInit {
  private readonly instances = inject(InstanceService);

  protected readonly state = signal<LoadState<InstancePage>>(loading());

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
   * Shortens an instance id for display.
   *
   * A full GUID in every row crowds out the columns an operator actually scans.
   * The full value stays in the cell's title attribute, so nothing is lost.
   */
  protected shortId(instance: Instance): string {
    return instance.id.slice(0, 8);
  }
}
