import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InstanceList } from './instance-list';
import { INSTANCE_STATUSES, InstancePage } from '../../api/models';
import { expectNoAccessibilityViolations } from '../../testing/accessibility';

/**
 * Issue #32 - Instance list view.
 *
 * Scenario: Instances are listed with status
 * Scenario: Failed instances are visually distinct
 */
describe('InstanceList', () => {
  let fixture: ComponentFixture<InstanceList>;
  let http: HttpTestingController;

  const page = (...items: Partial<InstancePage['items'][number]>[]): InstancePage => ({
    items: items.map((item, index) => ({
      id: `0000000${index}-0000-0000-0000-00000000000${index}`,
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
ownerNodeId: null,
leaseExpiresAt: null,
awaitingRecovery: false,
    retriedFromInstanceId: null,
      ...item,
    })),
    total: items.length,
    page: 1,
    pageSize: 50,
  });

  function respondWith(body: InstancePage): void {
    http.expectOne((request) => request.url === '/api/instances').flush(body);
    fixture.detectChanges();
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

  it('renders one row per instance', () => {
    respondWith(page({}, {}, {}));

    const rows = fixture.nativeElement.querySelectorAll('tbody tr');

    expect(rows.length).toBe(3);
  });

  it('shows id, workflow, status and start time in each row', () => {
    respondWith(page({ status: 'Running', currentStepName: 'charge' }));

    const row: HTMLElement = fixture.nativeElement.querySelector('tbody tr');
    const text = row.textContent ?? '';

    expect(text).toContain('order-fulfilment');
    expect(text).toContain('Running');
    expect(text).toContain('charge');
    expect(row.querySelector('code')?.textContent?.trim()).toBe('00000000');
  });

  it('keeps the full instance id available even though it is shortened', () => {
    // Truncating for density is fine; losing the value is not. An operator
    // correlating with a log needs the whole id.
    respondWith(page({}));

    // The title lives on the link, which is the element a user hovers.
    const link: HTMLElement = fixture.nativeElement.querySelector('tbody a');

    expect(link.getAttribute('title')).toBe('00000000-0000-0000-0000-000000000000');
  });

  it('links each row to its detail view', () => {
    // A real link rather than a row click handler: keyboard reachable,
    // focusable, and openable in a new tab.
    respondWith(page({}));

    const link: HTMLAnchorElement = fixture.nativeElement.querySelector('tbody a');

    expect(link.getAttribute('href')).toBe('/instances/00000000-0000-0000-0000-000000000000');
  });

  it('marks a failed row with failure styling', () => {
    respondWith(page({ status: 'Failed' }, { status: 'Completed' }));

    const rows: HTMLElement[] = Array.from(fixture.nativeElement.querySelectorAll('tbody tr'));

    expect(rows[0].classList).toContain('row-failed');
    expect(rows[1].classList).not.toContain('row-failed');
  });

  it('conveys status by text and glyph, not colour alone', () => {
    // The dashboard's primary job. A colour-blind operator scanning for
    // failures must be able to find them.
    respondWith(page({ status: 'Failed' }));

    const badge: HTMLElement = fixture.nativeElement.querySelector('app-status-badge');

    expect(badge.textContent).toContain('Failed');
    expect(badge.querySelector('.badge-glyph')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('renders timestamps as machine-readable time elements', () => {
    respondWith(page({}));

    const time: HTMLElement = fixture.nativeElement.querySelector('tbody time');

    expect(time.getAttribute('datetime')).toBe('2026-07-31T12:00:00+00:00');
  });

  it('renders a dash rather than blank when there is no current step', () => {
    // A blank cell reads as missing data. A completed instance genuinely has
    // no current step, and that is different.
    respondWith(page({ status: 'Completed', currentStepName: null }));

    const cells: HTMLElement[] = Array.from(fixture.nativeElement.querySelectorAll('tbody td'));

    expect(cells[cells.length - 1].textContent?.trim()).toBe('—');
  });

  it('uses a table with a caption and column headers', () => {
    respondWith(page({}));

    const table: HTMLElement = fixture.nativeElement.querySelector('table');
    const headers: HTMLElement[] = Array.from(table.querySelectorAll('th'));

    expect(table.querySelector('caption')).toBeTruthy();
    expect(headers.every((header) => header.getAttribute('scope') === 'col')).toBe(true);
  });

  it('has no accessibility violations', async () => {
    respondWith(page({ status: 'Failed' }, { status: 'Running' }));

    await expectNoAccessibilityViolations(fixture);
  });

  // ----------------------------------------------------------- #122 filter

  it('offers every status the engine can report', () => {
    // Built from INSTANCE_STATUSES, so a status added to the engine appears
    // here without anyone remembering to add it. A status missing from this
    // list is a status an operator cannot find.
    respondWith(page({}));

    const options: HTMLElement[] = Array.from(
      fixture.nativeElement.querySelectorAll('.status-filter option'),
    );

    const values = options.map((option) => option.getAttribute('value'));

    expect(values).toContain('Compensated');
    expect(values).toContain('CompensationFailed');

    // Plus an "any status" option, which is the default.
    expect(values.length).toBe(INSTANCE_STATUSES.length + 1);
  });

  it('requests only the chosen status', () => {
    respondWith(page({}));

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('.status-filter');
    select.value = 'Compensated';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const request = http.expectOne((r) => r.url === '/api/instances');

    expect(request.request.params.get('status')).toBe('Compensated');
    request.flush(page({ status: 'Compensated' }));
    fixture.detectChanges();
  });

  it('sends no status parameter when the filter is cleared', () => {
    // `?status=` would be an unrecognised status value and a 400, not "no
    // filter" - a distinction InstanceService already makes and this must not
    // undo.
    respondWith(page({}));

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('.status-filter');
    select.value = 'Failed';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    http.expectOne((r) => r.url === '/api/instances').flush(page({}));

    select.value = '';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const request = http.expectOne((r) => r.url === '/api/instances');

    expect(request.request.params.has('status')).toBe(false);
    request.flush(page({}));
    fixture.detectChanges();
  });

  it('keeps the filter visible when it matches nothing', () => {
    // Filtering to a status with no results must not hide the control that got
    // you there, or an operator is left on an empty page with no way back.
    respondWith(page({}));

    const select: HTMLSelectElement = fixture.nativeElement.querySelector('.status-filter');
    select.value = 'Compensated';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    http.expectOne((r) => r.url === '/api/instances').flush({
      items: [],
      total: 0,
      page: 1,
      pageSize: 50,
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.status-filter')).not.toBeNull();
  });
});