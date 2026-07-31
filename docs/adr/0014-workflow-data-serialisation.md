# ADR-0014: Workflow data is serialised with a type allow-list

**Status:** Accepted · **Milestone:** M2 · **Issue:** #15

## Context

ADR-0013 flagged that persisting workflow data would need a serialisation
format, and that "what happens to a value that cannot be serialised" was an
open problem. #15 has to answer it.

`IWorkflowData` holds `object`. A workflow's data shape is author-defined and
only known at runtime, so a text-backed provider must record enough to restore
the original type: without a tag, `42` and `"42"` are indistinguishable coming
back in.

That immediately raises the harder question. Storing a type name and resolving
it on read is the classic deserialisation remote-code-execution vector: whoever
can write to the store chooses which type the application constructs.

## Decision

**JSON, with each value tagged by its type name, and an allow-list controlling
which type names may be written or resolved.**

- `WorkflowDataSerializerOptions` seeds a list of BCL primitives an author would
  reach for without thinking about persistence: string, bool, the numeric types,
  `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `byte[]`.
- Anything else must be opted in with `Allow<T>()`.
- Serialising an unlisted type raises `WorkflowDataSerializationException`
  naming the key and type.
- **Deserialising never resolves a type by name unless it is on the list.** A
  tampered row can at worst produce a type the application already trusts.
- Type names are stored unqualified by assembly, so a row does not stop being
  readable when an assembly version changes.
- A null value is stored with a null type tag, keeping "explicitly cleared"
  distinct from "absent" - the contract ADR-0005 established.

**`InMemoryWorkflowStore` optionally round-trips through the serialiser.** The
conformance suite runs twice: once against the plain store, once against the
serialising one.

## Rationale for the double running the serialiser

The trap this closes is specific and common: a workflow that works perfectly in
tests against an in-memory double, then fails in production against a real
provider because it stored something unserialisable.

An in-memory store that keeps live object references cannot catch that. One that
round-trips through the same serialiser a text-backed provider uses will. So the
fast test suite surfaces the problem, and #17's provider is not the first thing
to discover it.

## Consequences

- A workflow storing an unregistered type fails **at the point of storage**,
  naming the key - not later, when a read cannot reconstruct it.
- Authors must register their own DTOs. That is friction, and it is the point:
  choosing what crosses the persistence boundary should be deliberate.
- The allow-list is per-serialiser, so a build that allows fewer types than the
  one that wrote a row will refuse to read it. The exception says so explicitly,
  because the alternative - silently dropping the value - would be worse.
- No polymorphism: a value stored as a base type is restored as whatever
  concrete type was written. Adequate for a data bag; revisit if a story needs
  interface-typed values.
- Records and simple DTOs work once registered. Types with reference cycles,
  delegates or unmanaged handles do not, and never will.
- The serialiser is unused by the plain in-memory store, so a project that
  never runs the serialising configuration still gets no protection. The suite
  running both configurations is what makes it non-optional in this repository.

## Alternatives considered

**Untagged JSON.** Everything comes back as `JsonElement` or `string`, and
`Get<int>` fails on data that was written as an `int`. Pushes the problem onto
every step author.

**`System.Text.Json` polymorphic serialisation with `$type`.** Effectively the
same design with the allow-list replaced by attributes on the types, which means
the persistence contract is scattered across DTO declarations rather than stated
in one place.

**Binary serialisation.** `BinaryFormatter` is obsolete and unsafe by design.
Other binary formats would work but are unreadable in a database, which matters
when diagnosing a stuck instance by querying the store directly.

**Allow any type by name.** Simplest and the actual vulnerability. Rejected
outright.

**A typed data class per workflow.** Compile-time safety and no allow-list, but
forces every workflow to declare a data type up front and complicates a generic
store. Noted in ADR-0005 as worth revisiting; still not warranted.
