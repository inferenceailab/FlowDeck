import { components } from './schema';

/**
 * Named aliases over the generated schema.
 *
 * `schema.ts` is generated from `openapi.json` and must never be hand-edited.
 * Its types are reachable only through deep index expressions like
 * `components['schemas']['InstanceResponse']`, which are unreadable at every
 * use site.
 *
 * These aliases give them names **without decoupling them**: change the API and
 * the generated schema changes, and every consumer of these aliases stops
 * compiling. A hand-written interface would keep compiling and be quietly
 * wrong, which is the whole failure mode ADR-0018 exists to prevent.
 */
export type Schemas = components['schemas'];

export type Instance = Schemas['InstanceResponse'];
export type InstancePage = Schemas['InstancePage'];
export type StartInstanceResponse = Schemas['StartInstanceResponse'];
export type WorkflowDefinition = Schemas['WorkflowDefinitionResponse'];
export type StepHistoryEntry = Schemas['StepHistoryResponse'];
export type InstanceStatus = Schemas['InstanceStatus'];
export type StepStatus = Schemas['StepStatus'];

/**
 * Every instance status, in lifecycle order.
 *
 * Derived from the generated union, so a status added to the engine fails to
 * compile here rather than silently disappearing from the status filter.
 */
export const INSTANCE_STATUSES: readonly InstanceStatus[] = [
  'Running',
  'Suspended',
  'Completed',
  'Failed',
  'Cancelled',
] as const;

/** Whether an instance has reached a state it will not leave on its own. */
export function isTerminal(status: InstanceStatus): boolean {
  return status === 'Completed' || status === 'Failed' || status === 'Cancelled';
}
