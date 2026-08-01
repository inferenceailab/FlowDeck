import {
  Instance,
  InstancePage,
  InstanceStatus,
  StepHistoryEntry,
  WorkflowBranch,
  WorkflowDefinitionDetail,
  WorkflowStep,
} from '../app/api/models';

/**
 * Fixtures the feature files describe in prose.
 *
 * Shared across features because scenarios talk about the same things — "three
 * instances", "an instance that failed at step B" — and defining them once
 * means one wording maps to one shape rather than to several that have quietly
 * drifted apart.
 */
export const anInstance = (overrides: Partial<Instance> = {}): Instance => ({
  id: '3f2a0000-0000-0000-0000-000000000001',
  definitionId: 'order-fulfilment',
  definitionVersion: 1,
  status: 'Completed',
  currentStepIndex: 0,
  currentStepName: null,
  createdAt: '2026-08-01T12:00:00+00:00',
  completedAt: '2026-08-01T12:00:05+00:00',
  failedStepName: null,
  errorType: null,
  errorMessage: null,
  ownerNodeId: null,
  leaseExpiresAt: null,

  // Computed server-side, so a scenario sets it rather than deriving it from a
  // timestamp the browser would compare against its own clock.
  awaitingRecovery: false,
  ...overrides,
});

export const aPageOf = (...items: Partial<Instance>[]): InstancePage => ({
  items: items.map((item, index) => ({
    ...anInstance(),
    id: `3f2a0000-0000-0000-0000-00000000000${index}`,
    ...item,
  })),
  total: items.length,
  page: 1,
  pageSize: 50,
});

export const aStep = (
  sequence: number,
  stepName: string,
  overrides: Partial<StepHistoryEntry> = {},
): StepHistoryEntry => ({
  sequence,
  stepName,
  startedAt: '2026-08-01T12:00:00+00:00',
  completedAt: '2026-08-01T12:00:01+00:00',
  durationMs: 1000,
  status: 'Success',
  attempt: 1,
  errorType: null,
  errorMessage: null,
  ...overrides,
});

export const DEFINITION_ID = 'order-fulfilment';

/**
 * A declared step, defaulting to the plain case: runs once, no undo, no
 * branches. A scenario overrides only the thing it is about, so a reader can
 * see what the scenario claims is significant.
 */
export const aWorkflowStep = (name: string, overrides: Partial<WorkflowStep> = {}): WorkflowStep => ({
  name,
  maxAttempts: 1,
  hasCompensation: false,
  branches: [],
  ...overrides,
});

export const aBranch = (
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

export const aDefinition = (...steps: WorkflowStep[]): WorkflowDefinitionDetail => ({
  id: DEFINITION_ID,
  version: 1,
  inputTypeName: null,
  steps,
});

export const INSTANCES_URL = '/api/instances';

/** The status a scenario names, typed rather than passed around as a string. */
export const statusOf = (name: string): InstanceStatus => name as InstanceStatus;
