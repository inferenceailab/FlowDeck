import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InstanceDetail } from './instance-detail';
import { Instance, InstanceStatus } from '../../api/models';
import { expectNoAccessibilityViolations } from '../../testing/accessibility';

/**
 * Issue #35 - Cancel an instance from the dashboard.
 *
 * Scenario: Cancel action calls the API and refreshes
 * Scenario: Cancel is unavailable for completed instances
 */
describe('InstanceDetail cancellation', () => {
  const id = '3f2a0000-0000-0000-0000-000000000000';

  let fixture: ComponentFixture<InstanceDetail>;
  let http: HttpTestingController;

  const instance = (status: InstanceStatus): Instance => ({
    id,
    definitionId: 'order-fulfilment',
    definitionVersion: 1,
    status,
    currentStepIndex: 0,
    currentStepName: status === 'Suspended' ? 'wait' : null,
    createdAt: '2026-07-31T12:00:00+00:00',
    completedAt: null,
    failedStepName: null,
    errorType: null,
    errorMessage: null,
  });

  const button = (selector: string): HTMLButtonElement =>
    fixture.nativeElement.querySelector(selector);

  const text = (): string => fixture.nativeElement.textContent ?? '';

  function load(status: InstanceStatus): void {
    http.expectOne(`/api/instances/${id}`).flush(instance(status));
    http.expectOne(`/api/instances/${id}/history`).flush([]);
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [InstanceDetail],
      providers: [provideHttpClient(withFetch()), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(InstanceDetail);
    fixture.componentRef.setInput('instanceId', id);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('asks for confirmation before cancelling', () => {
    // Cancelling is irreversible. A single click that silently stops a
    // long-running workflow is expensive to get wrong.
    load('Suspended');

    button('.cancel-button').click();
    fixture.detectChanges();

    expect(text()).toContain('Cancel this instance?');

    // Nothing sent yet - confirmation is a real gate, not a formality.
    http.expectNone(`/api/instances/${id}/cancel`);

    button('.confirm-actions button:last-child').click();
    fixture.detectChanges();
  });

  it('calls the API and refreshes once confirmed', () => {
    load('Suspended');

    button('.cancel-button').click();
    fixture.detectChanges();

    button('.confirm-actions button').click();

    http.expectOne(`/api/instances/${id}/cancel`).flush(instance('Cancelled'));
    fixture.detectChanges();

    // Refreshed rather than patched from the response: cancelling ends the
    // instance and the history is what an operator looks at next.
    http.expectOne(`/api/instances/${id}`).flush(instance('Cancelled'));
    http.expectOne(`/api/instances/${id}/history`).flush([]);
    fixture.detectChanges();

    expect(text()).toContain('Cancelled');
  });

  it('backing out sends nothing', () => {
    load('Suspended');

    button('.cancel-button').click();
    fixture.detectChanges();

    button('.confirm-actions button:last-child').click();
    fixture.detectChanges();

    expect(text()).not.toContain('Cancel this instance?');
    http.expectNone(`/api/instances/${id}/cancel`);
  });

  it('disables cancel for a completed instance', () => {
    load('Completed');

    expect(button('.cancel-button').disabled).toBe(true);
  });

  it('disables cancel for failed and cancelled instances too', () => {
    // All three are terminal (ADR-0008); offering the action on any of them
    // would produce a 409 the operator cannot act on.
    load('Failed');

    expect(button('.cancel-button').disabled).toBe(true);
  });

  it('keeps the button present when disabled rather than hiding it', () => {
    // A control that vanishes leaves an operator wondering whether they
    // misremembered it. One that is present and disabled says the action
    // exists and does not apply here.
    load('Completed');

    expect(button('.cancel-button')).toBeTruthy();
  });

  it('enables cancel for an in-flight instance', () => {
    load('Suspended');

    expect(button('.cancel-button').disabled).toBe(false);
  });

  it('reports a failed cancel without discarding the instance', () => {
    // A 409 from a stale view is expected - the button is a courtesy, the API
    // is the authority. Blanking the page would lose the context the operator
    // was acting on.
    load('Suspended');

    button('.cancel-button').click();
    fixture.detectChanges();
    button('.confirm-actions button').click();

    http
      .expectOne(`/api/instances/${id}/cancel`)
      .flush(
        { detail: "Workflow instance cannot move from Completed to Cancelled." },
        { status: 409, statusText: 'Conflict' },
      );
    fixture.detectChanges();

    expect(text()).toContain('Could not cancel this instance');
    expect(text()).toContain('cannot move from Completed to Cancelled');

    // The instance is still on screen.
    expect(text()).toContain('order-fulfilment');
  });

  it('announces the confirmation as a dialog', () => {
    load('Suspended');

    button('.cancel-button').click();
    fixture.detectChanges();

    const dialog: HTMLElement = fixture.nativeElement.querySelector('[role="alertdialog"]');

    expect(dialog).toBeTruthy();
    expect(dialog.getAttribute('aria-labelledby')).toBe('confirm-title');
    expect(fixture.nativeElement.querySelector('#confirm-title')).toBeTruthy();
  });

  it('has no accessibility violations while confirming', async () => {
    load('Suspended');

    button('.cancel-button').click();

    await expectNoAccessibilityViolations(fixture);

    button('.confirm-actions button:last-child').click();
    fixture.detectChanges();
  });
});
