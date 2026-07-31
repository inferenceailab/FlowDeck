# ADR-0015: Migrations are owned by the host, not shipped by the library

**Status:** Accepted · **Milestone:** M2 · **Issue:** #21

## Context

#21 requires schema migrations to be applied safely and idempotently. The
obvious reading is that FlowDeck ships a set of EF Core migrations that a host
applies on startup.

That runs into a hard fact: **EF Core migrations are provider-specific.** A
migration generated against PostgreSQL emits PostgreSQL DDL and is not valid
against SQLite or SQL Server. `FlowDeck.Persistence.EntityFrameworkCore`
deliberately depends only on `EntityFrameworkCore.Relational` so the host picks
its database (ADR-0010). Shipping migrations would silently undo that.

There is a second, sharper problem. The conformance suite runs against SQLite
(#17), and PostgreSQL is unverified (#78). Shipping PostgreSQL migrations that
have never been executed would be publishing untested DDL and calling it a
migration path.

## Decision

**The library ships no migrations. The host generates them for its own
provider.**

FlowDeck supplies `WorkflowStoreMigrator`, which:

- applies whatever migrations the host has defined, and reports which ones it
  applied
- is a no-op when the database is current, so it is safe on every start
- can report pending migrations **without applying them**, for a readiness
  probe (#29)
- offers `EnsureCreatedAsync` for tests and throwaway databases, documented as
  having no upgrade path

The model is defined once, in `WorkflowDbContext`. A host runs
`dotnet ef migrations add` against it with its own provider.

## Consequences

- No untested DDL is published. The alternative was shipping PostgreSQL
  migrations verified against nothing.
- The host does more work: one `dotnet ef migrations add` per schema change.
- A host on a provider FlowDeck has never seen still works, because nothing is
  provider-specific.
- **Model changes become a breaking change for hosts**, who must regenerate.
  That needs a release-note discipline this project does not yet have - worth an
  issue before the first release.
- `WorkflowStoreMigrator.MigrateAsync` returns an empty list here, because this
  build defines no migrations. It is still the correct call for a host that has
  some.
- Tests prove the mechanism - idempotency, non-destruction, side-effect-free
  reporting - rather than proving a particular migration works. That is the
  honest limit of what can be verified without shipping the migrations.

## Alternatives considered

**Ship PostgreSQL migrations.** Matches the deployment target and forces a
PostgreSQL dependency into a package that deliberately has none, while
publishing DDL no test has ever run.

**Ship one migration set per provider, in separate packages.** What mature
libraries do, and the right answer eventually. Premature now: there is one
deployment target, no release process, and #78 has not yet verified PostgreSQL
at all.

**`EnsureCreated` only.** Simple and has **no upgrade path** - it cannot alter
an existing schema, so the first model change after go-live would strand every
existing database. Offered for tests, never for production.

**Hand-written SQL scripts.** Provider-portable in principle, and duplicates the
model in a second place that will drift from the first.

## Revisit when

- #78 verifies PostgreSQL, making a shipped PostgreSQL migration set defensible
- FlowDeck has a release process, so a model change can be communicated rather
  than merely committed
