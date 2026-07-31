import { ComponentFixture, TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { INSTANCE_STATUSES, InstanceStatus } from '../../api/models';
import { StatusBadge } from './status-badge';

describe('StatusBadge', () => {
  let fixture: ComponentFixture<StatusBadge>;

  const render = (status: InstanceStatus): HTMLElement => {
    fixture.componentRef.setInput('status', status);
    fixture.detectChanges();

    return fixture.nativeElement.querySelector('.badge');
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [StatusBadge] }).compileComponents();
    fixture = TestBed.createComponent(StatusBadge);
  });

  it('gives every status its own glyph', () => {
    // ADR-0016: colour is never the only signal, so the glyph has to be
    // distinct per status. Two statuses sharing one makes them
    // indistinguishable in greyscale, which is the case the glyph exists for.
    const glyphs = INSTANCE_STATUSES.map(
      (status) => render(status).querySelector('.badge-glyph')?.textContent?.trim(),
    );

    expect(new Set(glyphs).size).toBe(INSTANCE_STATUSES.length);
  });

  it('never falls back to a placeholder glyph', () => {
    // The switch used to end in `default: return '•'`, so a status added to the
    // engine rendered as an anonymous dot rather than failing to compile.
    const glyphs = INSTANCE_STATUSES.map(
      (status) => render(status).querySelector('.badge-glyph')?.textContent?.trim(),
    );

    expect(glyphs).not.toContain('•');
  });

  it('labels the compensation statuses in words an operator reads', () => {
    // "CompensationFailed" is a wire value, not English. The badge is the
    // primary place an operator learns what happened.
    expect(render('Compensated').textContent).toContain('Rolled back');
    expect(render('CompensationFailed').textContent).toContain('Rollback failed');
  });

  it('gives every status its own colour class', () => {
    const classes = INSTANCE_STATUSES.map((status) => render(status).className);

    expect(new Set(classes).size).toBe(INSTANCE_STATUSES.length);
  });
});
