# ADR-0026: Definition version lifecycle

**Status:** Accepted · **Milestone:** M9 · **Issues:** #67

## Context

[ADR-0001](0001-definition-identity-includes-version.md) settled *identity*
versioning in M1: a definition is the pair `(Id, Version)`, the registry keys on
both, two versions coexist, and an instance pins its version at start so a
deployment cannot change what an in-flight instance executes.

That is the foundation, and everything after a new version deploys is still
undecided. Today a host can `Register` a v2 and there is no way to remove a v1,
no way to see whether anything is still running one, and no rule about what
happens if a host simply stops registering it — which is the ordinary way a
version disappears. An instance whose definition is no longer registered
**cannot be resumed at all**: `ResumeAsync` and the dispatcher both call
`registry.Get`, which throws. A crash-recovered instance of a retired version is
therefore permanently stuck, and nothing says so.

This ADR settles the lifecycle. Two decisions were the maintainer's and are
marked.

## Decisions

### 1. In-flight instances run to completion on the version they started

**Decided by the maintainer, 2026-08-01.**

There is **no migration**. An instance executes the shape it started with until
it settles, and two versions of one definition execute concurrently — on the same
node, in the same process.

The alternatives were considered and rejected, and what they would have bought is
worth stating rather than implying they were never on the table:

- **Migrating a suspended instance to a newer version** would let an operator fix
  a broken workflow without cancelling in-flight work. It also makes every removed
  or renamed step a case needing an answer, and it means rewriting a persisted
  position — which is the single easiest way to break NFR-1.
- **A patching API** (Temporal's model) lets one deployed definition branch on the
  version its instance started under, which is strictly more capable. It is also a
  new authoring concept every workflow author then has to understand, and #162 has
  only just given them branching at all.

The cost of deciding this way is real: **a bug in v1 stays in v1** for every
instance already running it. The only remedies are to wait, or to cancel and start
again on v2.

### 2. Retiring a version that instances still hold is refused

**Decided by the maintainer, 2026-08-01.**

Removing a definition version fails, loudly, while any non-terminal instance is
running it — and the failure says how many.

Today this hazard exists and is silent: a host that stops registering v1 leaves
every in-flight v1 instance unresumable, and nothing reports it. An operator
tidying up would discover it when a dispatcher failed to recover something, days
later.

The alternatives:

- **Tombstoning** — mark retired, start nothing new on it, let it disappear when
  the last instance settles. Gentler, and it needs a durable retired flag and a
  sweep to clear it, which is a persistence change for an operation that happens
  rarely.
- **Allowing it and failing the instances** is the most honest description of what
  the registry is, and converts an operator's cleanup into data loss they did not
  ask for.

Refusing is the one that cannot surprise anybody.

### 3. Retirement lives on the engine, not the registry

Taken without asking. `WorkflowRegistry` is a lookup with no persistence
dependency, and deciding whether a version is in use requires the store.

Putting the check on the registry would mean either giving it a store — making
every consumer of a lookup carry a database — or leaving the check to callers,
which makes decision 2 a convention rather than a rule. So `WorkflowEngine` owns
retirement, because it is the type that already holds both.

### 4. "In use" means non-terminal, counted by `(id, version)`

Taken without asking. An instance holds a version while it can still execute:
`Running` or `Suspended`. A terminal instance keeps its `DefinitionId` and
`DefinitionVersion` forever — history is not rewritten — and treating that as a
hold would mean no version could ever be retired.

`InstanceFilter` gains `DefinitionVersion` and an active-only flag. That is a
store-contract change, so the conformance suite gains cases and both providers
implement it. **The fifth field to reach that suite**, and the reason is the same
every time: a predicate a provider silently ignores returns the wrong answer
rather than an error, and here the wrong answer is "nothing is using this
version, go ahead and delete it".

### 5. Workflow data gets no schema version of its own

Taken without asking, and it is a consequence of decision 1 rather than a
separate choice.

Workflow data is only ever read by the definition version that wrote it, because
no instance ever changes version. A schema version would exist to describe data
crossing between definition versions, and nothing crosses.

If migration is ever added, this becomes a live question again — and it should be
answered then, against a real migration, rather than guessed at now.

## Consequences

- Two versions execute side by side, which already worked and was never asserted.
  It is a property the engine now states rather than happens to have.
- An operator can find out which versions are in use before touching them, which
  they currently cannot.
- A version cannot be removed while it matters, so the "stuck instance" hazard
  above becomes impossible rather than merely undocumented.
- A bug in a deployed version cannot be fixed for instances already running it.
  This is the price of decision 1 and it is documented in the guide, not glossed.
- Nothing here changes the persisted record. Retirement is a registry operation
  guarded by a store query; there is no new state to migrate (ADR-0015).

## Alternatives considered

**Do nothing, and document that hosts must not unregister a version.** The
cheapest option, and it makes NFR-1's protection depend on an operator reading a
sentence. Rejected: the failure it prevents is silent and delayed, which is the
combination worth spending code on.

**Refuse to *start* on a retired version but allow the removal.** A halfway
position that stops the bleeding without protecting what is already running.
Rejected because it protects the case that costs nothing — starting a new
instance, which the operator controls — and not the case that costs work.
