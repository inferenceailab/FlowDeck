import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InstanceDetail } from './instance-detail';
import { Instance, StepHistoryEntry } from '../../api/models';
import { expectNoAccessibilityViolations } from '../../testing/accessibility';

/**
 * Issue #33 - Instance detail view with step timeline.
 *
 * Scenario: Timeline reflects execution history
 * Scenario: The failing step is called out
 */
describe('InstanceDetail', () => {
  const id = '3f2a0000-0000-0000-0000-000000000000';

  let fixture: ComponentFixture<InstanceDetail>;
  let http: HttpTestingController;

  const instance = (overrides: Partial<Instance> = {}): Instance => ({
    id,
    definitionId: 'order-fulfilment',
    definitionVersion: 1,
    status: 'Completed',
    currentStepIndex: 2,
    currentStepName: null,
    createdAt: '2026-07-31T12:00:00+00:00',
    completedAt: '2026-07-31T12:00:05+00:00',
    failedStepName: null,
    errorType: null,
    errorMessage: null,
ownerNodeId: null,
leaseExpiresAt: null,
retriedFromInstanceId: null,
awaitingRecovery: false,
    ...overrides,
  });

  const step = (
    sequence: number,
    stepName: string,
    overrides: Partial<StepHistoryEntry> = {},
  ): StepHistoryEntry => ({
    sequence,
    stepName,
    startedAt: '2026-07-31T12:00:00+00:00',
    completedAt: '2026-07-31T12:00:01+00:00',
    durationMs: 1000,
    status: 'Success',
    attempt: 1,
    errorType: null,
    errorMessage: null,
    ...overrides,
  });

  const text = (): string => fixture.nativeElement.textContent ?? '';

  function respond(body: Instance, history: StepHistoryEntry[]): void {
    http.expectOne(`/api/instances/${id}`).flush(body);
    http.expectOne(`/api/instances/${id}/history`).flush(history);

    // The shape, chained off the instance now that the view draws the run on
    // it (#181). Answered with no steps: what the overlay marks has its own
    // scenarios in run-overlay.feature, and these tests are about the timeline.
    // Left unanswered it would simply fail http.verify() in afterEach.
    http.expectOne((request) => request.url.startsWith('/api/workflows/')).flush({
      id: body.definitionId,
      version: body.definitionVersion,
      inputTypeName: null,
      steps: [],
    });

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

  it('shows the timeline in execution order with outcomes', () => {
    respond(instance(), [step(1, 'validate'), step(2, 'charge'), step(3, 'ship')]);

    const entries: HTMLElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.timeline-entry .entry-step'),
    );

    expect(entries.map((entry) => entry.textContent?.trim())).toEqual([
      'validate',
      'charge',
      'ship',
    ]);
  });

  it('marks retried attempts so repeated rows are not read as duplicates', () => {
    // #107. Three rows for one step is ambiguous without this: it reads
    // identically to a rendering bug, or to a step re-entered by a resume.
    respond(instance({ status: 'Failed', failedStepName: 'charge' }), [
      step(1, 'charge', { attempt: 1, status: 'Failed' }),
      step(2, 'charge', { attempt: 2, status: 'Failed' }),
      step(3, 'charge', { attempt: 3, status: 'Failed' }),
    ]);

    const attempts: HTMLElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.timeline-entry .entry-attempt'),
    );

    expect(attempts.map((entry) => entry.textContent?.trim())).toEqual([
      'attempt 2',
      'attempt 3',
    ]);
  });

  it('does not label the first attempt', () => {
    // Every row on an ordinary run is attempt 1. Badging all of them would add
    // noise to the common case to serve the rare one.
    respond(instance(), [step(1, 'validate'), step(2, 'charge')]);

    expect(fixture.nativeElement.querySelectorAll('.entry-attempt').length).toBe(0);
  });

  it('marks rollback entries as rollback, not as ordinary steps', () => {
    // #122. Without this, "compensate:charge" appears in the timeline as a step
    // the author never declared, and the run reads as having executed
    // something that does not exist in the definition.
    respond(instance({ status: 'Compensated', failedStepName: 'ship' }), [
      step(1, 'charge'),
      step(2, 'ship', { status: 'Failed', errorMessage: 'no carrier' }),
      step(3, 'compensate:charge'),
    ]);

    const entries: HTMLElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.timeline-entry'),
    );

    expect(entries[2].classList).toContain('entry-rollback');

    // The prefix is a wire detail. The operator reads the step name.
    const names = entries.map((entry) => entry.querySelector('.entry-step')?.textContent?.trim());
    expect(names).toEqual(['charge', 'ship', 'charge']);

    expect(entries[2].textContent).toContain('rolled back');
    expect(entries[0].classList).not.toContain('entry-rollback');
  });

  it('calls out a partial rollback and names what failed', () => {
    // CompensationFailed is the one status that always needs a human, and the
    // thing they need is which undo did not happen.
    respond(instance({ status: 'CompensationFailed', failedStepName: 'ship' }), [
      step(1, 'charge'),
      step(2, 'ship', { status: 'Failed', errorMessage: 'no carrier' }),
      step(3, 'compensate:charge', {
        status: 'Failed',
        errorType: 'TimeoutException',
        errorMessage: 'gateway unreachable',
      }),
    ]);

    const alert = fixture.nativeElement.querySelector('.rollback-summary');

    expect(alert).not.toBeNull();
    expect(alert.textContent).toContain('charge');
    expect(alert.textContent).toContain('gateway unreachable');
  });

  it('does not claim a partial rollback when everything was undone', () => {
    respond(instance({ status: 'Compensated', failedStepName: 'ship' }), [
      step(1, 'charge'),
      step(2, 'ship', { status: 'Failed' }),
      step(3, 'compensate:charge'),
    ]);

    expect(fixture.nativeElement.querySelector('.rollback-summary')).toBeNull();
  });

  it('still states the original failure on a compensated instance', () => {
    // The rollback is what the engine did about it; the failure is why. An
    // operator opening a Compensated instance still needs the cause.
    respond(
      instance({
        status: 'Compensated',
        failedStepName: 'ship',
        errorType: 'InvalidOperationException',
        errorMessage: 'no carrier available',
      }),
      [step(1, 'ship', { status: 'Failed', errorMessage: 'no carrier available' })],
    );

    expect(text()).toContain('Failed at step ship');
    expect(text()).toContain('no carrier available');
  });

  it('disables cancel on a compensated instance', () => {
    // Terminal. Offering an action the API refuses tells the operator their
    // action failed on an instance that finished cleanly.
    //
    // Disabled rather than hidden: the button vanishing leaves an operator
    // wondering whether they misremembered it, while one that is present and
    // disabled says the action exists and does not apply here. That is a
    // decision this view already made, not something this story changes.
    respond(instance({ status: 'Compensated' }), []);

    const cancel: HTMLButtonElement = fixture.nativeElement.querySelector('.cancel-button');

    expect(cancel).not.toBeNull();
    expect(cancel.disabled).toBe(true);
  });

  it('calls out the failing step and its error', () => {
    respond(
      instance({
        status: 'Failed',
        failedStepName: 'charge',
        errorType: 'InvalidOperationException',
        errorMessage: 'card declined',
      }),
      [
        step(1, 'validate'),
        step(2, 'charge', {
          status: 'Failed',
          errorType: 'InvalidOperationException',
          errorMessage: 'card declined',
        }),
      ],
    );

    expect(text()).toContain('Failed at step charge');
    expect(text()).toContain('card declined');

    const failed: HTMLElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.entry-failed'),
    );

    expect(failed.length).toBe(1);
  });

  it('states the failure before the timeline, not buried in it', () => {
    // An operator opening this after an alert wants the answer first and the
    // evidence second.
    respond(
      instance({ status: 'Failed', failedStepName: 'charge', errorMessage: 'nope' }),
      [step(1, 'charge', { status: 'Failed', errorMessage: 'nope' })],
    );

    const summary: HTMLElement = fixture.nativeElement.querySelector('.failure-summary');
    const timeline: HTMLElement = fixture.nativeElement.querySelector('.timeline');

    expect(summary).toBeTruthy();
    expect(summary.compareDocumentPosition(timeline) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('marks each entry with an outcome word, not only a glyph', () => {
    // The marker is aria-hidden, so the word is what a screen reader reads and
    // what a colour-blind operator relies on (ADR-0016).
    respond(instance(), [step(1, 'validate', { status: 'Failed' })]);

    const entry: HTMLElement = fixture.nativeElement.querySelector('.timeline-entry');

    expect(entry.querySelector('.entry-status')?.textContent?.trim()).toBe('Failed');
    expect(entry.querySelector('.entry-marker')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('shows a step that ran twice as two numbered entries', () => {
    // A step re-entered after a resume genuinely executed twice. Collapsing
    // them would misreport the number of attempts.
    respond(instance(), [step(1, 'wait'), step(2, 'wait'), step(3, 'ship')]);

    const steps: HTMLElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.entry-step'),
    );

    expect(steps.map((s) => s.textContent?.trim())).toEqual(['wait', 'wait', 'ship']);
    expect(fixture.nativeElement.querySelector('ol')).toBeTruthy();
  });

  it('shows an empty timeline message rather than an empty list', () => {
    respond(instance({ status: 'Running', currentStepName: 'validate' }), []);

    expect(text()).toContain('No steps have executed yet');
    expect(fixture.nativeElement.querySelector('.timeline')).toBeNull();
  });

  it('loads the instance and its history as one view, not two', () => {
    // Fetching sequentially would show the instance and then pop the timeline
    // in a moment later, reading as a second load.
    http.expectOne(`/api/instances/${id}`);
    http.expectOne(`/api/instances/${id}/history`);

    expect(text()).toContain('Loading instance');

    http.verify();
  });

  it('shows an error state when either request fails', () => {
    // History is flushed first: forkJoin unsubscribes from its siblings the
    // moment one errors, so a request flushed after the failure would no
    // longer be outstanding and expectOne would not find it.
    http.expectOne(`/api/instances/${id}/history`).flush([]);
    http.expectOne(`/api/instances/${id}`).flush(
      { detail: 'No workflow instance with that id is known.' },
      { status: 404, statusText: 'Not Found' },
    );
    fixture.detectChanges();

    expect(text()).toContain('Could not load this instance');
    expect(text()).toContain('No workflow instance with that id is known.');
  });

  it('has no accessibility violations', async () => {
    respond(
      instance({ status: 'Failed', failedStepName: 'charge', errorMessage: 'nope' }),
      [step(1, 'validate'), step(2, 'charge', { status: 'Failed', errorMessage: 'nope' })],
    );

    await expectNoAccessibilityViolations(fixture);
  });
});