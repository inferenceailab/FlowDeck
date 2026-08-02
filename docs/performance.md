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

### Per-step cost does not amortise reliably

The per-step figures were expected to be the interesting pair, and they were —
for the opposite reason to the one predicted.

On this machine a ten-step instance costs about **4 µs** per step against a
one-step instance's **12 µs**: instance creation is paid once and spread. On a
contended CI runner the same measurement came out at **65 µs** per step against
**45 µs** — the longer instance cost *more* per step, not less.

Both are real. Every checkpoint rewrites the instance record, and history grows
as the instance goes, so a longer run does more work per step. Whether that
outweighs amortised creation depends on how fast the machine is relative to that
per-checkpoint work.

The first version of the guard asserted the ratio, on the amortisation reasoning
alone. CI failed it, which is the guard doing its job — just not the job it was
written for. **It now bounds per-step cost absolutely** and reports both numbers,
because the ratio encodes a model of the engine that does not hold.

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
- **Every step costs a store round trip, and the write grows.** That is
  ADR-0013's checkpoint model working as designed — at most one step of progress
  can be lost — and it puts a hard floor under per-step cost. It also means the
  floor *rises* over an instance's life, because each checkpoint rewrites the
  record and appends to a history that keeps getting longer. A long-running
  instance costs more per step than a short one.
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
