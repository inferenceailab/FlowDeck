import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InstanceList } from './instance-list';
import { InstancePage } from '../../api/models';
import { expectNoAccessibilityViolations } from '../../testing/accessibility';

/**
 * Issue #34 - Loading, empty and error states.
 *
 * Scenario: Loading state is shown while fetching
 * Scenario: Empty state is shown when there are no instances
 * Scenario: Error state is shown when the API fails
 */
describe('InstanceList states', () => {
  let fixture: ComponentFixture<InstanceList>;
  let http: HttpTestingController;

  const emptyPage: InstancePage = { items: [], total: 0, page: 1, pageSize: 50 };

  const onePage: InstancePage = {
    ...emptyPage,
    total: 1,
    items: [
      {
        id: '00000000-0000-0000-0000-000000000000',
        definitionId: 'order-fulfilment',
        definitionVersion: 1,
        status: 'Completed',
        currentStepIndex: 0,
        currentStepName: null,
        createdAt: '2026-07-31T12:00:00+00:00',
        completedAt: '2026-07-31T12:00:05+00:00',
        failedStepName: null,
        errorType: null,
        errorMessage: null,
      },
    ],
  };

  const text = (): string => fixture.nativeElement.textContent ?? '';

  function pending() {
    return http.expectOne((request) => request.url === '/api/instances');
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [InstanceList],
      providers: [provideHttpClient(withFetch()), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(InstanceList);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('shows a loading state while the request is in flight', () => {
    // Deliberately not flushed: this is the state during the request.
    const request = pending();

    expect(text()).toContain('Loading instances');
    expect(fixture.nativeElement.querySelector('table')).toBeNull();

    request.flush(emptyPage);
  });

  it('marks the region busy while loading, so it is announced', () => {
    // A sighted user sees a spinner. Without aria-busy and a live region, a
    // screen reader user gets no indication anything is happening at all.
    const request = pending();
    const region: HTMLElement = fixture.nativeElement.querySelector('[aria-live]');

    expect(region.getAttribute('aria-busy')).toBe('true');
    expect(region.getAttribute('aria-live')).toBe('polite');

    request.flush(emptyPage);
    fixture.detectChanges();

    expect(region.getAttribute('aria-busy')).toBe('false');
  });

  it('shows an empty state rather than an empty table', () => {
    // A table with headers and no rows reads as broken. This says nothing is
    // wrong, there is simply nothing yet.
    pending().flush(emptyPage);
    fixture.detectChanges();

    expect(text()).toContain('No workflow instances yet');
    expect(fixture.nativeElement.querySelector('table')).toBeNull();
  });

  it('shows an error state when the API fails', () => {
    pending().flush(
      { type: 'about:blank', title: 'Server error', status: 500, detail: 'Everything is on fire.' },
      { status: 500, statusText: 'Internal Server Error' },
    );
    fixture.detectChanges();

    expect(text()).toContain('Could not load instances');
    expect(fixture.nativeElement.querySelector('table')).toBeNull();
  });

  it('surfaces the problem details message, not a generic apology', () => {
    // The API returns RFC 9457 problem details whose `detail` names what went
    // wrong. Replacing it with "an error occurred" discards the only part an
    // operator can act on.
    pending().flush(
      { title: 'Not found', status: 404, detail: "No workflow definition 'order' is registered." },
      { status: 404, statusText: 'Not Found' },
    );
    fixture.detectChanges();

    expect(text()).toContain("No workflow definition 'order' is registered.");
  });

  it('says the API was unreachable when the request never arrived', () => {
    // Status 0 means the network failed, not that the API said anything. "The
    // API returned 0" would be nonsense to an operator.
    pending().error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    fixture.detectChanges();

    expect(text()).toContain('Could not reach the FlowDeck API');
  });

  it('announces a failure as an alert rather than politely', () => {
    pending().flush({ detail: 'nope' }, { status: 500, statusText: 'Internal Server Error' });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="alert"]')).toBeTruthy();
  });

  it('offers a retry that actually re-requests', () => {
    // An error state with no way out forces a page reload, which loses any
    // other context the operator had.
    pending().flush({ detail: 'nope' }, { status: 500, statusText: 'Internal Server Error' });
    fixture.detectChanges();

    const retry: HTMLButtonElement = fixture.nativeElement.querySelector('.state-error button');
    retry.click();
    fixture.detectChanges();

    // A second request proves the retry is wired, not decorative.
    pending().flush(onePage);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('tbody tr').length).toBe(1);
  });

  it('has no accessibility violations while loading', async () => {
    const request = pending();

    await expectNoAccessibilityViolations(fixture);

    request.flush(emptyPage);
  });

  it('has no accessibility violations in the empty state', async () => {
    pending().flush(emptyPage);

    await expectNoAccessibilityViolations(fixture);
  });

  it('has no accessibility violations in the error state', async () => {
    pending().flush({ detail: 'nope' }, { status: 500, statusText: 'Internal Server Error' });

    await expectNoAccessibilityViolations(fixture);
  });
});
