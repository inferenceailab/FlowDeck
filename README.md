# FlowDeck

A generic workflow execution engine for .NET, with an operator dashboard.

Workflow steps are written in C#. The engine handles everything around that
code: sequencing it, giving it somewhere to keep state, surviving restarts,
retrying what is worth retrying, and showing an operator what happened.

> **Status: early, but working end to end.** M1–M4 complete — 293 backend tests
> and 53 frontend tests. A workflow can be defined in C#, executed, persisted,
> resumed after a restart, driven over HTTP and watched in an Angular dashboard.
>
> **There is no authentication.** Anything that can reach the API can start,
> inspect and cancel workflows. No retry, no compensation, single node only. See
> [known limitations](docs/architecture.md#known-limitations).

## What it looks like

```csharp
public sealed class GreetWorkflow : IWorkflowDefinition
{
    public string Id => "greet";

    public int Version => 1;

    public void Build(IWorkflowBuilder builder) =>
        builder.AddStep("say-hello", () => new SayHello());
}

var registry = new WorkflowRegistry();
registry.Register(new GreetWorkflow());

var engine = new WorkflowEngine(registry);
var instance = await engine.StartAsync("greet", version: 1);
// instance.Status == InstanceStatus.Completed
```

Full walkthrough: [Defining a workflow](docs/guides/defining-a-workflow.md).

## Documentation

| | |
| --- | --- |
| [Requirements](docs/requirements.md) | Scope, functional and non-functional requirements |
| [Architecture](docs/architecture.md) | Components, execution model, limitations |
| [Frontend architecture](docs/frontend-architecture.md) | Dashboard shape and decisions |
| [Implementation plan](docs/implementation-plan.md) | Roadmap and status |
| [Decision records](docs/adr/README.md) | Why things are the way they are |
| [Prior art](docs/prior-art.md) | What is borrowed from other engines, and what differs |
| [Defining a workflow](docs/guides/defining-a-workflow.md) | Usage guide |
| [Writing a persistence provider](docs/guides/writing-a-persistence-provider.md) | Implementing `IWorkflowStore` |
| [HTTP API](docs/api.md) | Endpoints, and why they are shaped that way |
| [API error contract](docs/api-errors.md) | What every error response means |
| [Deployment](deploy/README.md) | CI/CD and homelab setup |
| [Security](SECURITY.md) | Reporting vulnerabilities, enabled protections |

## Building

Requires the **.NET 10 SDK** (pinned in `global.json`).

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes --severity warn
```

Frontend (Angular 22, requires **Node 24**):

```sh
cd src/frontend
npm ci
npm test -- --watch=false
npm run lint
npm run build
```

## Contributing

Every change goes through a pull request. `main` blocks force-pushes,
deletions, non-linear history and unsigned commits, with no bypass.

After cloning, enable the secret-scanning hook:

```sh
git config core.hooksPath .githooks
scoop install gitleaks    # or https://github.com/gitleaks/gitleaks#installing
```

The hook refuses to run if `gitleaks` is missing rather than letting unscanned
commits through.

Work is tracked as GitHub issues grouped into milestones. Stories carry
Given/When/Then acceptance criteria; tests are written before implementation.
Non-obvious decisions get an [ADR](docs/adr/README.md) in the same pull request.

## Prior art

Design patterns are borrowed deliberately from **WorkflowCore** (step model),
**Hangfire** (durable state, distributed locking), **Elsa v3** (definition
versioning, suspension) and **Octopus Deploy** (dashboard UX).

No source code from any of them is copied. Some API *vocabulary* does overlap
with WorkflowCore, which is recorded openly in
[ADR-0011](docs/adr/0011-api-vocabulary-borrowed-from-workflowcore.md).
[Prior art](docs/prior-art.md) sets out what differs, what does not, and what
was deliberately left behind.