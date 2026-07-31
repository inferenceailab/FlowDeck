import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { InstanceService } from '../../api/instance.service';
import { Instance, InstancePage } from '../../api/models';
import { StatusBadge } from '../../components/status-badge/status-badge';

/**
 * Lists workflow instances.
 *
 * State lives in signals on the component and is fetched through
 * {@link InstanceService} (ADR-0018). Loading, empty and error states are #34;
 * this story renders the rows.
 */
@Component({
  selector: 'app-instance-list',
  imports: [DatePipe, StatusBadge],
  templateUrl: './instance-list.html',
  styleUrl: './instance-list.css',
})
export class InstanceList implements OnInit {
  private readonly instances = inject(InstanceService);

  protected readonly page = signal<InstancePage | null>(null);

  ngOnInit(): void {
    this.instances.list().subscribe((page) => this.page.set(page));
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
