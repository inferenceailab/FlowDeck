import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { InstanceList } from './instance-list';
import { InstancePage } from '../../api/models';
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
      providers: [provideHttpClient(), provideHttpClientTesting()],
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

    const code: HTMLElement = fixture.nativeElement.querySelector('tbody code');

    expect(code.getAttribute('title')).toBe('00000000-0000-0000-0000-000000000000');
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
});
