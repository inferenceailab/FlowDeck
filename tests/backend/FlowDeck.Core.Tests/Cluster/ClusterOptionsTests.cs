using FlowDeck.Core.Cluster;

namespace FlowDeck.Core.Tests.Cluster;

/// <summary>
/// Issue #144 - cluster configuration that would misbehave quietly.
/// </summary>
/// <remarks>
/// A unit test rather than a scenario: this is about rejecting a bad setting at
/// startup, not about behaviour an operator would recognise as a story.
/// </remarks>
public class ClusterOptionsTests
{
    [Fact]
    public void The_defaults_are_valid()
    {
        // Guards every other test here. If the defaults were invalid, every
        // node would fail to start and the guards below would be untestable
        // for the wrong reason.
        new ClusterOptions().Validate();
    }

    [Fact]
    public void A_renewal_interval_at_or_above_the_lease_is_rejected()
    {
        // The setting that produces a cluster which thrashes and looks like a
        // network problem: the lease has already lapsed by the time a healthy
        // node tries to renew it, so it hands its work to a peer every cycle.
        var options = new ClusterOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            RenewalInterval = TimeSpan.FromSeconds(10),
        };

        var ex = Assert.Throws<ArgumentException>(options.Validate);

        Assert.Contains("before renewing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_renewal_interval_below_the_lease_is_accepted()
    {
        new ClusterOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            RenewalInterval = TimeSpan.FromSeconds(9),
        }.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_duration_is_rejected(int seconds)
    {
        Assert.Throws<ArgumentException>(
            new ClusterOptions { LeaseDuration = TimeSpan.FromSeconds(seconds) }.Validate);

        Assert.Throws<ArgumentException>(
            new ClusterOptions { PollInterval = TimeSpan.FromSeconds(seconds) }.Validate);
    }

    [Fact]
    public void A_blank_node_id_is_rejected()
    {
        // A blank id would make every node indistinguishable, so each would
        // treat every other node's leases as its own - the opposite of what
        // claiming is for.
        Assert.Throws<ArgumentException>(new ClusterOptions { NodeId = "  " }.Validate);
    }

    [Fact]
    public void The_default_node_id_distinguishes_processes()
    {
        // A restarted process must not inherit its predecessor's leases
        // (ADR-0023 decision 5), so the default id includes the process.
        var id = new ClusterOptions().NodeId;

        Assert.Contains(Environment.MachineName, id, StringComparison.Ordinal);
        Assert.Contains(
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            id,
            StringComparison.Ordinal);
    }
}
