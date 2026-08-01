import { Routes } from '@angular/router';

/**
 * Application routes.
 *
 * Views are lazily loaded so the shell renders before their code is fetched.
 * With two views that saves little; establishing it now means it does not have
 * to be retrofitted once the designer (#183) arrives.
 *
 * Each route sets a title, so a browser tab and the accessible page name say
 * where you are rather than all reading "flowdeck-dashboard".
 */
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'instances',
  },
  {
    path: 'instances',
    title: 'Instances - FlowDeck',
    loadComponent: () => import('./views/instance-list/instance-list').then((m) => m.InstanceList),
  },
  {
    // Deliberately a sibling of the list rather than a child route. The detail
    // view fetches by id and does not need the list loaded, so nesting would
    // couple them for no benefit.
    path: 'instances/:instanceId',
    title: 'Instance - FlowDeck',
    loadComponent: () =>
      import('./views/instance-detail/instance-detail').then((m) => m.InstanceDetail),
  },
  {
    path: 'workflows',
    title: 'Workflows - FlowDeck',
    loadComponent: () => import('./views/workflow-list/workflow-list').then((m) => m.WorkflowList),
  },
  {
    // A sibling of the list for the same reason the instance detail is: it
    // fetches by id and does not need the list loaded.
    path: 'workflows/:definitionId',
    title: 'Workflow - FlowDeck',
    loadComponent: () =>
      import('./views/workflow-detail/workflow-detail').then((m) => m.WorkflowDetail),
  },
  {
    // A wildcard rather than a silent redirect home: a mistyped URL should say
    // so, not quietly land somewhere plausible and leave the operator
    // wondering why the page is not what they expected.
    path: '**',
    title: 'Not found - FlowDeck',
    loadComponent: () => import('./views/not-found/not-found').then((m) => m.NotFound),
  },
];
