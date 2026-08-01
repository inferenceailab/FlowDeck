using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #28 - Publish an OpenAPI document.
///
/// Scenario: OpenAPI document is served
/// </summary>
public class OpenApiDocumentTests
{
    private static async Task<JsonElement> DocumentAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/openapi/v1.json");

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_openapi_document_is_served()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        // When I GET /openapi/v1.json
        using var response = await client.GetAsync("/openapi/v1.json");

        // Then the response status is 200 OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_document_lists_every_public_endpoint()
    {
        // The clause that stops this being a smoke test. A document that omits
        // an endpoint is worse than none: a generated client silently lacks the
        // operation, and nobody finds out until runtime.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        var paths = (await DocumentAsync(client)).GetProperty("paths");

        var expected = new (string Path, string Method)[]
        {
            ("/api/workflows/{definitionId}/instances", "post"),
            ("/api/workflows", "get"),
            ("/api/workflows/{definitionId}", "get"),
            ("/api/instances/{instanceId}", "get"),
            ("/api/instances", "get"),
            ("/api/instances/{instanceId}/cancel", "post"),
        };

        foreach (var (path, method) in expected)
        {
            Assert.True(
                paths.TryGetProperty(path, out var operations),
                $"OpenAPI document is missing the path {path}");

            Assert.True(
                operations.TryGetProperty(method, out _),
                $"OpenAPI document is missing {method.ToUpperInvariant()} {path}");
        }
    }

    [Fact]
    public async Task Operations_carry_the_names_the_endpoints_declare()
    {
        // operationId is what client generators turn into method names. Letting
        // it default produces names like "GetApiInstancesByInstanceId".
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        var document = await DocumentAsync(client);
        var raw = document.ToString();

        foreach (var operationId in new[]
        {
            "StartWorkflowInstance", "ListWorkflowDefinitions", "GetWorkflowDefinition",
            "GetWorkflowInstance", "ListWorkflowInstances", "CancelWorkflowInstance",
        })
        {
            Assert.Contains(operationId, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_document_is_valid_openapi_with_the_pieces_a_generator_needs()
    {
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        var document = await DocumentAsync(client);

        Assert.True(document.TryGetProperty("openapi", out var version));
        Assert.StartsWith("3.", version.GetString()!, StringComparison.Ordinal);

        Assert.True(document.TryGetProperty("info", out var info));
        Assert.False(string.IsNullOrWhiteSpace(info.GetProperty("title").GetString()));

        Assert.True(document.TryGetProperty("paths", out _));
    }

    [Fact]
    public async Task The_document_is_served_outside_Development()
    {
        // The template only maps OpenAPI in Development, and FlowDeck maps it
        // unconditionally: a description a client cannot fetch from the running
        // server is one that goes stale.
        //
        // The environment is set explicitly. WebApplicationFactory hosts in
        // Development by default, so the original version of this test ran
        // *inside* Development and proved nothing at all - it was byte-identical
        // to the test above, which is how SonarAnalyzer found it.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"))
            .CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Every_endpoint_the_router_knows_about_appears_in_the_document()
    {
        // Stronger than the fixed list above: this catches an endpoint added
        // later and never described, which is exactly how an OpenAPI document
        // rots.
        using var factory = new FlowDeckApiFactory().With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        var paths = (await DocumentAsync(client)).GetProperty("paths");
        var described = paths.EnumerateObject()
            .Select(property => Normalise(property.Name))
            .ToHashSet(StringComparer.Ordinal);

        var routed = factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))

            // Health probes and the document itself are infrastructure, not part
            // of the API a client generates against.
            .Where(path => !path.StartsWith("/health", StringComparison.Ordinal))
            .Where(path => !path.StartsWith("/openapi", StringComparison.Ordinal))
            .Select(Normalise)
            .ToHashSet(StringComparer.Ordinal);

        var missing = routed.Except(described).ToArray();

        Assert.True(
            missing.Length == 0,
            $"routed but undocumented: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Reduces a route pattern to the form the OpenAPI document uses.
    /// </summary>
    /// <remarks>
    /// Two differences, both cosmetic:
    ///
    /// <list type="bullet">
    /// <item>Route constraints - the router says <c>{id:guid}</c>, the document
    /// says <c>{id}</c> and expresses the type in the parameter schema.</item>
    /// <item>A trailing slash - a group-relative route registered as <c>""</c>
    /// reports as <c>/api/instances/</c>, which the document normalises to
    /// <c>/api/instances</c>.</item>
    /// </list>
    ///
    /// Normalising here rather than loosening the assertion: the point of this
    /// test is to catch an endpoint that is genuinely undescribed, and it would
    /// be useless if it also fired on two spellings of the same path.
    /// </remarks>
    private static string Normalise(string path)
    {
        var withoutConstraints = System.Text.RegularExpressions.Regex.Replace(
            path, @"\{(\w+):[^}]+\}", "{$1}");

        return withoutConstraints.Length > 1
            ? withoutConstraints.TrimEnd('/')
            : withoutConstraints;
    }
}
