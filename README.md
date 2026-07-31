# FlowDeck

A generic workflow execution engine for .NET, with an operator dashboard.

Workflow steps are written in C#. The engine handles everything around that
code: sequencing it, giving it somewhere to keep state, surviving restarts,
retrying what is worth retrying, and showing an operator what happened.

> **Status: early.** M1 (core engine primitives) is complete — 81 tests.
> Everything is in memory: instances are lost on process restart, there is no
> HTTP API, no dashboard, no retry and no persistence. See
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
| [Implementation plan](docs/implementation-plan.md) | Roadmap and status |
| [Decision records](docs/adr/README.md) | Why things are the way they are |
| [Defining a workflow](docs/guides/defining-a-workflow.md) | Usage guide |
| [Deployment](deploy/README.md) | CI/CD and homelab setup |
| [Security](SECURITY.md) | Reporting vulnerabilities, enabled protections |

## Building

Requires the **.NET 10 SDK** (pinned in `global.json`).

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes --severity warn
```

Angular 22 frontend arrives with M4 and is not yet scaffolded.

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

## Inspiration

Design patterns are borrowed deliberately from **WorkflowCore** (step and saga
model), **Hangfire** (durable state, distributed locking), **Elsa v3**
(definition versioning, suspension) and **Octopus Deploy** (dashboard UX).
