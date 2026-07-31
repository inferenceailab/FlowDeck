namespace FlowDeck.Core.Persistence;

/// <summary>
/// How long terminal instances are kept before being purged.
/// </summary>
/// <remarks>
/// Retention is off unless configured. An engine that silently deleted history
/// after some built-in period would destroy an audit trail nobody agreed to
/// lose.
/// </remarks>
public sealed record RetentionPolicy
{
    /// <summary>Keep terminal instances for this long after they finish.</summary>
    public required TimeSpan KeepFor { get; init; }

    /// <summary>Retain for a number of days.</summary>
    public static RetentionPolicy Days(int days) =>
        days > 0
            ? new RetentionPolicy { KeepFor = TimeSpan.FromDays(days) }
            : throw new ArgumentOutOfRangeException(
                nameof(days), days, "Retention must be positive; use no policy to retain indefinitely.");
}

/// <summary>
/// Deletes terminal instances once they fall outside the retention window.
/// </summary>
/// <remarks>
/// Deliberately a plain method rather than a hosted background service. What
/// triggers it - a timer, a cron job, an operator - is a hosting decision, and
/// baking one in would force it on every consumer. #41 can schedule it.
/// </remarks>
public sealed class InstancePurger(IWorkflowStore store, RetentionPolicy policy, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Removes every terminal instance that finished longer ago than the
    /// retention window.
    /// </summary>
    /// <returns>How many instances were removed.</returns>
    public Task<int> PurgeAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);

        var cutoff = this.timeProvider.GetUtcNow() - policy.KeepFor;

        return store.PurgeAsync(cutoff, cancellationToken);
    }
}
