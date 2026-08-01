import { provideHttpClient, withFetch } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { WorkflowDetail } from './workflow-detail';
import { WorkflowBranch, WorkflowDefinitionDetail, WorkflowStep } from '../../api/models';
import { expectNoAccessibilityViolations } from '../../testing/accessibility';

/**
 * Issue #172 - Render a definition's shape in the dashboard.
 *
 * The five acceptance scenarios execute in
 * `src/features/workflow-detail.feature`. What is here is everything that
 * layer cannot see: the loading and error states, the accessibility check
 * ADR-0016 requires, and the recursion below the first level of branching -
 * which every scenario stops short of, so a renderer that flattened at depth 2
 * would satisfy all of them.
 */
describe('WorkflowDetail', () => {
  const id = 'order-fulfilment';

  let fixture: ComponentFixture<WorkflowDetail>;
  let http: HttpTestingController;

  const step = (name: string, overrides: Partial<WorkflowStep> = {}): WorkflowStep => ({
    name,
    maxAttempts: 1,
    hasCompensation: false,
    branches: [],
    ...overrides,
  });

  const branch = (
    name: string,
    steps: WorkflowStep[],
    overrides: Partial<WorkflowBranch> = {},
  ): WorkflowBranch => ({
    name,
    isConditional: false,
    isParallel: false,
    steps,
    ...overrides,
  });

  const definition = (...steps: WorkflowStep[]): WorkflowDefinitionDetail => ({
    id,
    version: 1,
    inputTypeName: null,
    steps,
  });

  const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';

  const query = (selector: string): HTMLElement | null =>
    (fixture.nativeElement as HTMLElement).querySelector(selector);

  function respond(body: WorkflowDefinitionDetail): void {
    http.expectOne(`/api/workflows/${id}`).flush(body);
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WorkflowDetail],
      providers: [provideHttpClient(withFetch()), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    fixture = TestBed.createComponent(WorkflowDetail);
    fixture.componentRef.setInput('definitionId', id);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  it('nests a branch inside a branch', () => {
    // The shape is a tree, not a list of lists. Every acceptance scenario stops
    // at one level of branching, so a renderer that gave up below that would
    // pass all five and quietly lose half of a real workflow.
    respond(
      definition(
        step('check-stock', {
          branches: [
            branch('in-stock', [
              step('charge', {
                branches: [branch('declined', [step('notify-customer')])],
              }),
            ]),
          ],
        }),
      ),
    );

    const inner = query('.branch .shape-step .branch .shape-step .step-name');

    expect(inner?.textContent?.trim()).toBe('notify-customer');
  });

  it('says a branch is chosen by the data without claiming what the data says', () => {
    // The API reports that a branch carries a condition, never the condition -
    // it is a compiled delegate. Rendering an invented description would put a
    // guess in front of an operator as though it were the definition.
    respond(
      definition(
        step('price', {
          branches: [
            branch('automatic', [step('auto-approve')]),
            branch('manual', [step('approve')], { isConditional: true }),
          ],
        }),
      ),
    );

    const labels = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('.branch'),
    ).map((element) => element.querySelector('.branch-condition') !== null);

    expect(labels).toEqual([false, true]);
  });

  it('names the input type only when the workflow takes one', () => {
    respond({ ...definition(step('work')), inputTypeName: 'OrderRequest' });

    expect(query('.definition-input')?.textContent).toContain('OrderRequest');
  });

  it('omits the input row for a workflow that takes none', () => {
    // Absent, not "takes nothing". A row an operator has to read to learn there
    // is nothing to read is worse than no row.
    respond(definition(step('work')));

    expect(query('.definition-input')).toBeNull();
  });

  it('shows a loading state while the definition is in flight', () => {
    expect(text()).toContain('Loading workflow');

    // Still outstanding: this is the state before an answer arrives, not after
    // an empty one, and the two would render identically without this.
    expect(http.expectOne(`/api/workflows/${id}`)).toBeTruthy();
  });

  it('shows the problem detail when the definition cannot be loaded', () => {
    http.expectOne(`/api/workflows/${id}`).flush(
      { detail: "No workflow definition 'order-fulfilment' is registered." },
      { status: 404, statusText: 'Not Found' },
    );
    fixture.detectChanges();

    expect(text()).toContain('Could not load this workflow');

    // The API's own message, which names the definition. "An error occurred"
    // leaves an operator with nothing to act on.
    expect(text()).toContain("No workflow definition 'order-fulfilment' is registered.");
  });

  it('has no accessibility violations', async () => {
    respond(
      definition(
        step('check-stock', {
          branches: [
            branch('in-stock', [step('charge', { maxAttempts: 3, hasCompensation: true })]),
            branch('backorder', [step('notify')], { isConditional: true }),
          ],
        }),
        step('confirm'),
      ),
    );

    await expectNoAccessibilityViolations(fixture);
  });
});
