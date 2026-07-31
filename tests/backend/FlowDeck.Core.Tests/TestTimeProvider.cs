// A controllable clock for tests.
//
// This was hand-rolled to avoid a dependency, on the stated grounds that the
// engine needed exactly one capability from it: a UTC clock under test control.
// Retry (#105) needs a second - controllable *timers*, so that `Task.Delay`
// with a TimeProvider does not sleep for real - and that is substantially more
// than a clock. Overriding only GetUtcNow left CreateTimer falling through to
// the base implementation, so a retry test genuinely waited three seconds while
// its comment claimed otherwise.
//
// Microsoft.Extensions.TimeProvider.Testing implements both correctly and is
// test-only, so it never reaches a shipped artefact. ADR-0010 asks for a
// dependency to be justified rather than assumed; this is the justification.
//
// Aliased rather than replaced at every call site: FakeTimeProvider already
// exposes the constructor and Advance signature the existing tests use, so the
// change is one line instead of thirty-four edits.
global using TestTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;
