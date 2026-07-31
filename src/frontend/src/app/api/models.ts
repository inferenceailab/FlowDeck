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
 * This previously claimed that a status added to the engine would fail to
 * compile here. It would not have: `readonly InstanceStatus[]` accepts any
 * subset of the union quite happily, so #120's two new statuses were added to
 * the engine and this list stayed green while silently dropping them from the
 * status filter. The check below is the guarantee the comment was promising.
 */
export const INSTANCE_STATUSES = [
  'Running',
  'Suspended',
  'Completed',
  'Failed',
  'Cancelled',
  'Compensated',
  'CompensationFailed',
] as const satisfies readonly InstanceStatus[];

/**
 * Fails to compile if the engine gains a status not listed above.
 *
 * `Exclude` is empty only when every member of the union appears in the array,
 * and a non-empty result violates the `extends never` constraint.
 */
type AssertNoStatusMissing<T extends never> = T;
export type AllStatusesListed = AssertNoStatusMissing<
  Exclude<InstanceStatus, (typeof INSTANCE_STATUSES)[number]>
>;

/**
 * Whether an instance has reached a state it will not leave on its own.
 *
 * Compensated and CompensationFailed are terminal: rollback happens *before*
 * the instance settles, so by the time either is reported there is nothing left
 * to do. Treating them as in-flight would offer a Cancel the API refuses.
 */
export function isTerminal(status: InstanceStatus): boolean {
  return (
    status === 'Completed' ||
    status === 'Failed' ||
    status === 'Cancelled' ||
    status === 'Compensated' ||
    status === 'CompensationFailed'
  );
}
