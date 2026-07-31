import { Component, computed, input } from '@angular/core';
import { InstanceStatus } from '../../api/models';

/**
 * Renders an instance status.
 *
 * Three signals, not one: a coloured dot, a **glyph**, and the status word.
 * Colour alone would leave the dashboard's primary information unreadable to a
 * colour-blind operator, and status is the single thing this dashboard exists
 * to convey (ADR-0016).
 *
 * The glyph is `aria-hidden`; the word is the accessible name, so a screen
 * reader announces "Failed" rather than "cross Failed".
 */
@Component({
  selector: 'app-status-badge',
  template: `<span class="badge" [class]="'badge-' + status().toLowerCase()">
    <span class="badge-glyph" aria-hidden="true">{{ glyph() }}</span>
    <span class="badge-label">{{ status() }}</span>
  </span>`,
  styleUrl: './status-badge.css',
})
export class StatusBadge {
  readonly status = input.required<InstanceStatus>();

  /**
   * A distinct shape per status.
   *
   * Deliberately shapes rather than colours-of-icons: they remain
   * distinguishable in greyscale, in high-contrast mode, and to anyone who
   * cannot tell the palette apart.
   */
  protected readonly glyph = computed(() => {
    switch (this.status()) {
      case 'Running':
        return '▶'; // ▶
      case 'Suspended':
        return '⏸'; // ⏸
      case 'Completed':
        return '✓'; // ✓
      case 'Failed':
        return '✕'; // ✕
      case 'Cancelled':
        return '⊘'; // ⊘
      default:
        return '•'; // •
    }
  });
}
