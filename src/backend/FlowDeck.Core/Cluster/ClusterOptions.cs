namespace FlowDeck.Core.Cluster;

/// <summary>
/// How this node participates in a cluster.
/// </summary>
/// <remarks>
/// Every node runs the same code and polls for the same work — there is no
/// leader and no election (ADR-0023). The only thing that distinguishes nodes
/// is <see cref="NodeId"/>.
/// </remarks>
public sealed record ClusterOptions
{
    /// <summary>
    /// Identifies this node while it holds a lease.
    /// </summary>
    /// <remarks>
    /// Defaults to machine and process, so a restarted process gets a
    /// <b>different</b> id. That is deliberate: the old process's in-flight
    /// work was abandoned when it died, and letting its successor silently
    /// adopt those leases would skip exactly the recovery they exist for.
    /// </remarks>
    public string NodeId { get; init; } =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>
    /// How long a claim survives without renewal.
    /// </summary>
    /// <remarks>
    /// The operational knob. Too short and healthy work is stolen from a node
    /// that is merely slow; too long and recovery after a crash waits. Thirty
    /// seconds with renewal every ten leaves three chances to renew before a
    /// peer may take over.
    /// </remarks>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a working node renews its lease.</summary>
    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often this node looks for claimable work.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Rejects settings that would misbehave quietly.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Renewal is not comfortably shorter than the lease, or an interval is not
    /// positive.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(this.NodeId))
        {
            throw new ArgumentException("NodeId must not be blank.");
        }

        if (this.LeaseDuration <= TimeSpan.Zero || this.RenewalInterval <= TimeSpan.Zero
            || this.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("Lease and interval durations must be positive.");
        }

        // A renewal interval at or above the lease duration means the lease has
        // already lapsed by the time the node tries to renew it, so a healthy
        // node would hand its work to a peer every cycle. Failing fast here
        // beats a cluster that thrashes and looks like a network problem.
        if (this.RenewalInterval >= this.LeaseDuration)
        {
            throw new ArgumentException(
                $"RenewalInterval ({this.RenewalInterval}) must be shorter than LeaseDuration "
                    + $"({this.LeaseDuration}), or a healthy node loses its lease before renewing.");
        }
    }
}
