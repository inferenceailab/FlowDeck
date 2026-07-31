# ADR-0009: The in-memory store is temporary

**Status:** Accepted · **Milestone:** M1 · **Issue:** #11

## Context

#11 required the engine to answer "what is the status of instance X?", which
means keeping instances somewhere. M2 will make that durable. The question is
what to build at M1.

The temptation is to design the persistence abstraction now — providers,
transactions, migrations — so M2 is "just" an implementation.

## Decision

Build `InMemoryInstanceStore` behind an `IInstanceStore` interface with exactly
the four operations #11 needs: `Add`, `Get`, `TryGet`, `GetAll`.

State the limitations explicitly in the code and here, rather than implying a
durability the implementation does not provide.

## Consequences

- #11 ships without inventing a persistence shape no test constrains.
- `IInstanceStore` is the seam #17 substitutes an EF Core provider into.
- **Instances are lost on process restart.** Tracked by #13/#14.
- **The store is unbounded** and grows for the process lifetime. Tracked by #20.
- Runtime execution state (compiled steps, data, input) lives on the engine, so
  `ResumeAsync` works only in the process that started the instance.
- The interface will almost certainly need to change for M2 — async signatures,
  concurrency tokens, query predicates. That is expected: four honest methods
  are easier to grow than a speculative abstraction is to correct.

## Alternatives considered

**Design the full persistence abstraction now.** Async, versioned, queryable,
transactional. Every one of those decisions would be made without a test
constraining it — exactly what the epic placeholders in M5–M9 exist to prevent.

**No store; return the instance and forget it.** Was the M1 state before #11
and cannot satisfy "query an in-flight instance".

**Go straight to EF Core.** Couples the core engine to a database for a
milestone whose scope is engine primitives, and makes every M1 test need one.
