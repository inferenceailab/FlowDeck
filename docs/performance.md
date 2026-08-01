# Performance baseline

A **baseline, not a target** ([ADR-0027](adr/0027-multi-tenancy-and-performance.md)).
These are numbers FlowDeck currently produces, recorded with the hardware they
were produced on. Nothing here commits the project to meeting a figure.

The point of writing them down is that "is it fast enough" was previously an
opinion nobody could be wrong about.

## What is measured, and why those three

| Measurement | The question it answers |
| --- | --- |
| Instances per second, end to end | The headline, and the one that moves when anything on the hot path changes |
| Cost per step | The engine checkpoints after **every** step ([ADR-0013](adr/0013-persistence-model.md)), so this is where throughput is spent and where a regression appears first |
| Backlog recovery | How long a dispatcher takes to clear abandoned work. M6's machinery has no visible cost otherwise |

Measured against the **in-memory store**, deliberately. The EF Core numbers would
mostly measure SQLite, and the question here is what the *engine* costs. A real
deployment's throughput is dominated by its database and by the step bodies the
author wrote — neither of which this measures, and neither of which FlowDeck
controls.

## Recorded baseline

Measured 2026-08-02, on the machine below, by
`Features/Performance/Baseline.feature`.

| Measurement | Value |
| --- | --- |
| Instances per second (3 steps, in-memory) | ~55,000 /s |
| One-step instance, end to end | ~12 µs |
| Ten-step instance, per step | ~4 µs |
| Clearing a 50-instance recovery backlog | ~0.01 s |

**Hardware:** Windows 11 Pro, x64, 28 logical processors, .NET 10 preview
(`10.0.400-preview.0.26322.102`). A developer machine, not a server, and not the
homelab FlowDeck deploys to.

The per-step figures are the interesting pair: a ten-step instance costs about
4 µs per step against a one-step instance's 12 µs, because instance creation is
paid once and spread across the steps. A scenario asserts that relationship
holds — if per-step cost ever stops amortising, something has started scaling
with step count that should not.

## The guard, and why it is loose

`Baseline.feature` fails the build if throughput falls below **20 instances per
second** — roughly 2,700× below what is measured above.

That looseness is the trade, not an oversight. These scenarios run on
GitHub-hosted runners whose speed varies between runs, and a guard that fails for
reasons unrelated to the engine gets deleted rather than investigated, which
leaves nothing at all. It is set to catch the kind of regression a change to
checkpointing or claiming causes — an order of magnitude — not the kind a busy
runner causes.

**It will not catch a 2× regression.** If that matters later, the answer is a
benchmark harness with a stable machine behind it, not a tighter number on a
shared runner.

## Known bottlenecks, named rather than measured

None of these is fixed by this baseline, and each is where the engine would
degrade first under real load:

- **A retry backoff blocks its branch.** `Task.Delay` inside the execution loop,
  left open by [ADR-0020](adr/0020-retry-semantics.md). A workflow with long
  backoffs holds a worker for the duration.
- **Every step costs a store round trip.** That is ADR-0013's checkpoint model
  working as designed — at most one step of progress can be lost — and it puts a
  hard floor under per-step cost that no amount of tuning removes.
- **The dispatcher polls on an interval.** Recovery latency is bounded below by
  `PollInterval`, not by how fast the store answers.
- **Checkpoints serialise through one writer per instance**
  ([ADR-0024](adr/0024-branching-and-parallel-execution.md)). Parallel branches
  overlap where the time goes and queue where the truth lives, so a fork of very
  fast steps sees less benefit than a fork of slow I/O.

## See also

- [ADR-0027](adr/0027-multi-tenancy-and-performance.md) — why a baseline rather
  than a target, and why multi-tenancy is out of scope
- [Architecture](architecture.md)
