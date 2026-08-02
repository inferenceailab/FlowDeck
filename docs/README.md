# FlowDeck Documentation

| Document | Read it for |
| --- | --- |
| [Requirements](requirements.md) | What FlowDeck is meant to do, and explicitly not do |
| [Architecture](architecture.md) | How it is built, and where it is knowingly incomplete |
| [Frontend architecture](frontend-architecture.md) | Dashboard shape, and the decisions made before building it |
| [Implementation plan](implementation-plan.md) | Milestone roadmap and current status |
| [Decision records](adr/README.md) | Why things are the way they are |
| [Performance baseline](performance.md) | What it currently does, on what hardware, and where it degrades first |
| [Prior art](prior-art.md) | What is borrowed from WorkflowCore, Hangfire and Elsa - and what differs |
| [Defining a workflow](guides/defining-a-workflow.md) | How to use the library |
| [Writing a persistence provider](guides/writing-a-persistence-provider.md) | Implementing `IWorkflowStore` |
| [Observing FlowDeck](guides/observing-flowdeck.md) | Every log, metric and span it emits — and what it never emits |
| [Operating FlowDeck](guides/operating-flowdeck.md) | The actions available when a workflow misbehaves, and what each costs |
| [HTTP API](api.md) | Endpoints, and why they are shaped that way |
| [API error contract](api-errors.md) | What every error response means |
| [Deployment](../deploy/README.md) | CI/CD and homelab setup |
| [Security policy](../SECURITY.md) | Reporting vulnerabilities, enabled protections |

## Where to start

**Using the library** → [Defining a workflow](guides/defining-a-workflow.md).

**Running it** → [Operating FlowDeck](guides/operating-flowdeck.md) and
[Observing FlowDeck](guides/observing-flowdeck.md), then
[Deployment](../deploy/README.md).

**Contributing** → [Architecture](architecture.md), then
[decision records](adr/README.md). The ADRs explain constraints that are not
obvious from the code.

**Assessing the project** → [Implementation plan](implementation-plan.md) for
status, then the *Known limitations* section of
[Architecture](architecture.md). Both state what does not work as plainly as
what does.

## Conventions

- Documentation describes **what exists**. Planned work is labelled as such.
- Known limitations are stated explicitly, with the issue tracking each one.
- Non-obvious decisions get an ADR, in the same pull request as the change.
