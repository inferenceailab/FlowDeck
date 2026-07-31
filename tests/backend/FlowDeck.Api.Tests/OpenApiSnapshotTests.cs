using System.Text.Json;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Keeps the committed OpenAPI document in step with the one the API serves.
/// </summary>
/// <remarks>
/// The frontend generates its API client from <c>src/frontend/openapi.json</c>
/// (ADR-0018). That file has to be committed, because a build step cannot start
/// the API to fetch it — and a committed copy of a live document is exactly the
/// kind of thing that silently rots.
///
/// <para>
/// So this test fails when they diverge. A backend change that alters the
/// contract breaks the build here, rather than surfacing as a runtime error in
/// front of an operator.
/// </para>
///
/// <para>
/// To accept a deliberate change:
/// <code>
/// $env:FLOWDECK_UPDATE_OPENAPI = "1"; dotnet test; Remove-Item Env:FLOWDECK_UPDATE_OPENAPI
/// </code>
/// then regenerate the client with <c>npm run generate:api</c> and commit both.
/// </para>
/// </remarks>
public class OpenApiSnapshotTests
{
    private static string SnapshotPath =>
        Path.Combine(FindRepositoryRoot(), "src", "frontend", "openapi.json");

    [Fact]
    public async Task The_committed_document_matches_what_the_api_serves()
    {
        using var factory = new FlowDeckApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();

        // Reformatted rather than compared raw, so insignificant whitespace or
        // property ordering cannot fail the test and train people to ignore it.
        var served = Normalise(await response.Content.ReadAsStringAsync());

        if (Environment.GetEnvironmentVariable("FLOWDECK_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(SnapshotPath, served);
            return;
        }

        Assert.True(
            File.Exists(SnapshotPath),
            $"No committed OpenAPI document at {SnapshotPath}. "
                + "Run with FLOWDECK_UPDATE_OPENAPI=1 to create it.");

        var committed = Normalise(await File.ReadAllTextAsync(SnapshotPath));

        Assert.True(
            served == committed,
            "The committed OpenAPI document is out of date with the API. "
                + "If the change is intended, re-run with FLOWDECK_UPDATE_OPENAPI=1, "
                + "regenerate the frontend client, and commit both.");
    }

    private static string Normalise(string json) =>
        JsonSerializer.Serialize(
            JsonSerializer.Deserialize<JsonElement>(json),
            new JsonSerializerOptions { WriteIndented = true });

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FlowDeck.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
