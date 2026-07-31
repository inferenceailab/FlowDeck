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
    <span class="badge-label">{{ label() }}</span>
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
    const status = this.status();

    switch (status) {
      case 'Running':
        return '▶';
      case 'Suspended':
        return '⏸';
      case 'Completed':
        return '✓';
      case 'Failed':
        return '✕';
      case 'Cancelled':
        return '⊘';
      case 'Compensated':
        return '↩';
      case 'CompensationFailed':
        return '⚠';
      default:
        return assertExhaustive(status);
    }
  });

  /**
   * What an operator reads.
   *
   * The status name is the wire value, and two of them are not English:
   * "CompensationFailed" describes an engine state, while an operator is
   * asking what happened to their workflow. The badge is where they find out,
   * so it says "Rollback failed".
   */
  protected readonly label = computed(() => {
    const status = this.status();

    switch (status) {
      case 'Compensated':
        return $localize`Rolled back`;
      case 'CompensationFailed':
        return $localize`Rollback failed`;
      default:
        return status;
    }
  });
}

/**
 * Fails to compile if a status is added and not handled above.
 *
 * The switch previously ended in `default: return '•'`, so #120's two new
 * statuses rendered as an anonymous dot and nothing complained. A silent
 * fallback turns "we forgot this case" into "this case looks deliberate".
 */
function assertExhaustive(status: never): never {
  throw new Error(`Unhandled instance status: ${String(status)}`);
}
