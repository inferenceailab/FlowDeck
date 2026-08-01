import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InstanceList } from './instance-list';
import { Instance, InstancePage, InstanceStatus } from '../../api/models';

/**
 * Issue #36 - Live status updates without manual refresh.
 *
 * Scenario: Status changes appear without a page reload
 */
describe('InstanceList live updates', () => {
  let fixture: ComponentFixture<InstanceList>;
  let http: HttpTestingController;

  const instance = (status: InstanceStatus): Instance => ({
    id: '00000000-0000-0000-0000-000000000000',
    definitionId: 'order-fulfilment',
    definitionVersion: 1,
    status,
    currentStepIndex: 0,
    currentStepName: status === 'Running' ? 'charge' : null,
    createdAt: '2026-07-31T12:00:00+00:00',
    completedAt: null,
    failedStepName: null,
    errorType: null,
    errorMessage: null,
ownerNodeId: null,
leaseExpiresAt: null,
awaitingRecovery: false,
  });

  const pageOf = (status: InstanceStatus): InstancePage => ({
    items: [instance(status)],
    total: 1,
    page: 1,
    pageSize: 50,
  });

  const text = (): string => fixture.nativeElement.textContent ?? '';

  /**
   * The table body only.
   *
   * The whole component's text is no longer a safe thing to assert absence
   * against: #122's status filter lists every status name as an option, so
   * "Running" appears on the page whether or not any instance is running.
   */
  const rowText = (): string =>
    fixture.nativeElement.querySelector('tbody')?.textContent ?? '';

  const listRequest = () => http.expectOne((request) => request.url === '/api/instances');

  beforeEach(async () => {
    // Fake timers so the refresh interval is stepped deliberately rather than
    // waited on. A test that sleeps for the real interval is slow and flaky.
    vi.useFakeTimers();

    await TestBed.configureTestingModule({
      imports: [InstanceList],
      providers: [provideHttpClient(withFetch()), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(InstanceList);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    http.verify();
    vi.useRealTimers();
  });

  it('shows a status change without a page reload', () => {
    listRequest().flush(pageOf('Running'));
    fixture.detectChanges();

    expect(text()).toContain('Running');

    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs);

    listRequest().flush(pageOf('Completed'));
    fixture.detectChanges();

    expect(rowText()).toContain('Completed');
    expect(rowText()).not.toContain('Running');
  });

  it('keeps the status filter across a background refresh', () => {
    // #122. The list polls every five seconds, so a refresh that dropped the
    // filter would silently repopulate the table with everything moments after
    // an operator narrowed it.
    listRequest().flush(pageOf('Running'));
    fixture.detectChanges();

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('.status-filter');
    select.value = 'CompensationFailed';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    listRequest().flush(pageOf('CompensationFailed'));
    fixture.detectChanges();

    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs);

    const refresh = listRequest();

    expect(refresh.request.params.get('status')).toBe('CompensationFailed');

    refresh.flush(pageOf('CompensationFailed'));
    fixture.detectChanges();
  });

  it('does not flash the loading state on every refresh', () => {
    // The reason refresh is not simply load(). Setting the loading state on
    // each tick would replace the table with a spinner every few seconds, so a
    // row an operator was reading would vanish under them.
    listRequest().flush(pageOf('Running'));
    fixture.detectChanges();

    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs);

    expect(text()).not.toContain('Loading instances');
    expect(fixture.nativeElement.querySelector('table')).toBeTruthy();

    listRequest().flush(pageOf('Running'));
  });

  it('keeps the last good data when a refresh fails', () => {
    // A single dropped poll is not worth discarding good data for. The next
    // tick retries.
    listRequest().flush(pageOf('Running'));
    fixture.detectChanges();

    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs);
    listRequest().flush({ detail: 'nope' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(text()).toContain('Running');
    expect(text()).not.toContain('Could not load instances');
  });

  it('recovers on the next tick after a failed refresh', () => {
    listRequest().flush(pageOf('Running'));
    fixture.detectChanges();

    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs);
    listRequest().flush({ detail: 'nope' }, { status: 500, statusText: 'Server Error' });

    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs);
    listRequest().flush(pageOf('Completed'));
    fixture.detectChanges();

    expect(text()).toContain('Completed');
  });

  it('does not stack requests behind a slow response', () => {
    // Without the in-flight guard, a response slower than the interval would
    // queue a new request every tick and pile up indefinitely.
    listRequest().flush(pageOf('Running'));
    fixture.detectChanges();

    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs);
    const inFlight = listRequest();

    // Two more ticks pass while the first refresh is still outstanding.
    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs * 2);

    http.expectNone((request) => request.url === '/api/instances');

    inFlight.flush(pageOf('Completed'));
    fixture.detectChanges();

    expect(text()).toContain('Completed');
  });

  it('stops polling once the view is destroyed', () => {
    // Otherwise the timer outlives the view and keeps requesting for a page
    // nobody is looking at, forever.
    listRequest().flush(pageOf('Running'));
    fixture.detectChanges();

    fixture.destroy();

    vi.advanceTimersByTime(InstanceList.RefreshIntervalMs * 3);

    http.expectNone((request) => request.url === '/api/instances');
  });
});