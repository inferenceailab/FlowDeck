namespace FlowDeck.Core.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test moves it.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking a dependency on
/// Microsoft.Extensions.TimeProvider.Testing: the engine needs exactly one
/// capability here - a UTC clock under test control - and a package would add
/// a supply-chain dependency to earn it.
/// </remarks>
internal sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => this.now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), "Time does not run backwards.");
        }

        this.now = this.now.Add(by);
    }
}
