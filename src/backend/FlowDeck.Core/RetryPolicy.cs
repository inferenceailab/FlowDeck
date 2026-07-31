namespace FlowDeck.Core;

/// <summary>
/// How the delay before a retry grows with the attempt number.
/// </summary>
public enum BackoffStrategy
{
    /// <summary>The same delay before every attempt.</summary>
    Fixed = 0,

    /// <summary>Doubles with each attempt.</summary>
    Exponential = 1,

    /// <summary>
    /// Doubles with each attempt, then randomises within that window.
    /// </summary>
    /// <remarks>
    /// The strategy to reach for. Exponential without jitter synchronises every
    /// instance that failed at the same moment, so they all retry together and
    /// hit the recovering service simultaneously.
    /// </remarks>
    ExponentialWithJitter = 2,
}

/// <summary>
/// How many times a step is retried, and how long to wait between attempts.
/// </summary>
/// <remarks>
/// Retry is opt-in — see <see cref="None"/> and ADR-0020. Silently retrying a
/// step an author believed ran once converts a visible failure into duplicated
/// side effects.
///
/// <para>
/// <b>A retry re-runs the whole step.</b> A step that charges a card and is
/// retried charges twice. The engine cannot detect that; the step must be
/// idempotent, and only its author can make it so.
/// </para>
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>
    /// How many times the step may execute in total, including the first.
    /// </summary>
    /// <remarks>
    /// Total attempts rather than "retries", because off-by-one arguments about
    /// whether 3 retries means 3 or 4 executions are a reliable source of
    /// surprise. <c>MaxAttempts = 3</c> means the step runs at most 3 times.
    /// </remarks>
    public required int MaxAttempts { get; init; }

    /// <summary>Delay before the second attempt; the base for growth.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ceiling on the computed delay.
    /// </summary>
    /// <remarks>
    /// Without a cap, exponential growth reaches hours within a dozen attempts
    /// and an instance appears hung.
    /// </remarks>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(1);

    public BackoffStrategy Backoff { get; init; } = BackoffStrategy.ExponentialWithJitter;

    /// <summary>
    /// No retry: the step executes once and a failure is final.
    /// </summary>
    /// <remarks>
    /// The default for any step that does not declare otherwise, and the way a
    /// step opts out of a workflow-level default.
    /// </remarks>
    public static RetryPolicy None { get; } = new() { MaxAttempts = 1 };

    /// <summary>A policy retrying with exponential backoff and jitter.</summary>
    public static RetryPolicy ExponentialBackoff(
        int maxAttempts,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null) =>
        new()
        {
            MaxAttempts = Validated(maxAttempts),
            BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1),
            MaxDelay = maxDelay ?? TimeSpan.FromMinutes(1),
            Backoff = BackoffStrategy.ExponentialWithJitter,
        };

    /// <summary>A policy retrying with a constant delay.</summary>
    public static RetryPolicy FixedDelay(int maxAttempts, TimeSpan delay) =>
        new()
        {
            MaxAttempts = Validated(maxAttempts),
            BaseDelay = delay,
            MaxDelay = delay,
            Backoff = BackoffStrategy.Fixed,
        };

    /// <summary>Whether another attempt is allowed after <paramref name="attemptsSoFar"/>.</summary>
    public bool AllowsAnotherAttempt(int attemptsSoFar) => attemptsSoFar < this.MaxAttempts;

    /// <summary>
    /// How long to wait before attempt number <paramref name="nextAttempt"/>.
    /// </summary>
    /// <param name="nextAttempt">
    /// One-based. The second execution of a step is attempt 2, so the first
    /// delay is computed for 2.
    /// </param>
    /// <param name="random">
    /// Supplied so jitter is deterministic under test. Randomness that cannot
    /// be pinned makes a backoff test either flaky or vacuous.
    /// </param>
    public TimeSpan DelayBefore(int nextAttempt, Random? random = null)
    {
        if (nextAttempt <= 1)
        {
            // The first execution is not a retry.
            return TimeSpan.Zero;
        }

        var exponent = nextAttempt - 2;

        var raw = this.Backoff switch
        {
            BackoffStrategy.Fixed => this.BaseDelay,
            _ => this.BaseDelay * Math.Pow(2, exponent),
        };

        var capped = raw > this.MaxDelay ? this.MaxDelay : raw;

        if (this.Backoff != BackoffStrategy.ExponentialWithJitter)
        {
            return capped;
        }

        // Full jitter: anywhere in (0, capped]. Spreads retries across the
        // whole window rather than clustering them near the top of it, which a
        // narrow jitter band would do.
        var source = random ?? Random.Shared;
        var factor = source.NextDouble();

        // Never zero: a retry that waits no time at all is not a backoff.
        return capped * Math.Max(factor, 0.01);
    }

    private static int Validated(int maxAttempts) =>
        maxAttempts >= 1
            ? maxAttempts
            : throw new ArgumentOutOfRangeException(
                nameof(maxAttempts), maxAttempts, "A policy must allow at least one attempt.");
}
