# ADR-0001: Definition identity includes version

**Status:** Accepted · **Milestone:** M1 · **Issue:** #1

## Context

A workflow definition needs an identifier so instances can be traced back to
what produced them. The obvious choice is a single string id.

But workflows are long-running. An instance started on Monday may still be
executing on Friday, and a deployment on Wednesday may change the definition.
If identity is the id alone, that instance is now executing a definition that
no longer matches the one it started under.

## Decision

Identity is the pair `(Id, Version)`. `WorkflowRegistry` keys on both.
Registering the same pair twice raises `DuplicateDefinitionException`. Two
versions of the same workflow coexist in the registry.

An instance pins its definition version at start and records it, so it is
always answerable which exact definition an instance is running.

Ids compare **ordinally**. A workflow id is a machine identifier, not display
text; culture must not decide whether two ids match.

## Consequences

- In-flight instances are unaffected by deploying a new version.
- Every lookup must supply a version, which is more verbose. `GetLatest` exists
  for callers that genuinely want the newest.
- Version migration for in-flight instances becomes a real, tractable question
  rather than an impossibility. Tracked by #43.
- The registry can hold many versions of the same workflow, so a long-lived
  process accumulates definitions. Acceptable: definitions are small and
  registered at startup.

## Alternatives considered

**Id alone, version as metadata.** Simpler lookups, but a redeploy silently
changes what running instances execute. Rejected as a correctness problem.

**Content hash as identity.** Precise, but unreadable in dashboards and logs,
and any cosmetic change produces a new identity.

**Immutable definitions, no versioning.** Would force a new id per change,
pushing versioning into naming conventions where nothing can enforce it.
