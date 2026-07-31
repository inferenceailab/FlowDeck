using System.Net;
using System.Net.Http.Json;

namespace FlowDeck.Api.Tests;

/// <summary>
/// Issue #30 - Register a workflow definition over HTTP.
///
/// Scenario: Registered definitions are listed
/// </summary>
/// <remarks>
/// The issue is titled "register ... over HTTP" but its scenario only requires
/// listing, and its intent is an operator confirming what a deployment
/// registered. Definitions are C# classes registered at startup, so there is
/// nothing to POST. Authoring definitions over the wire is #40's question, not
/// this one's.
/// </remarks>
public class ListDefinitionsEndpointTests
{
    [Fact]
    public async Task Registered_definitions_are_listed_with_ids_and_versions()
    {
        // Given two registered definitions
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow("order-fulfilment"))
            .With(new SimpleWorkflow("shipment"));
        using var client = factory.CreateClient();

        // When I GET /api/workflows
        using var response = await client.GetAsync("/api/workflows");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Then both definitions are returned with their ids and versions
        var definitions = await response.Content.ReadFromJsonAsync<WorkflowDefinitionResponse[]>();

        Assert.Equal(2, definitions!.Length);
        Assert.Contains(definitions, d => d.Id == "order-fulfilment" && d.Version == 1);
        Assert.Contains(definitions, d => d.Id == "shipment" && d.Version == 1);
    }

    [Fact]
    public async Task Every_version_of_a_definition_is_listed_separately()
    {
        // Identity is (id, version) - ADR-0001. Collapsing versions would hide
        // exactly what an operator checks after a deployment.
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow("versioned", 1))
            .With(new SimpleWorkflow("versioned", 2));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workflows");
        var definitions = await response.Content.ReadFromJsonAsync<WorkflowDefinitionResponse[]>();

        Assert.Equal(2, definitions!.Length);
        Assert.Equal([1, 2], definitions.Select(d => d.Version).Order());
    }

    [Fact]
    public async Task A_definition_declaring_input_reports_its_type_name()
    {
        // So a client can tell a POST needs a body before it sends an empty one
        // and gets a 400 back.
        using var factory = new FlowDeckApiFactory()
            .With(new TypedWorkflow([]))
            .With(new SimpleWorkflow());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workflows");
        var definitions = await response.Content.ReadFromJsonAsync<WorkflowDefinitionResponse[]>();

        Assert.NotNull(definitions);
        Assert.Equal("OrderRequest", definitions.Single(d => d.Id == "typed").InputTypeName);
        Assert.Null(definitions.Single(d => d.Id == "simple").InputTypeName);
    }

    [Fact]
    public async Task No_assembly_qualified_names_are_exposed()
    {
        // An assembly-qualified name tells a caller the assembly, version and
        // public key of the deployment. The type name alone is what they need.
        using var factory = new FlowDeckApiFactory().With(new TypedWorkflow([]));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workflows");
        var json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Version=", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PublicKeyToken", json, StringComparison.Ordinal);
        Assert.DoesNotContain("FlowDeck.Api.Tests", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_no_definitions_returns_an_empty_list()
    {
        using var factory = new FlowDeckApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workflows");
        var definitions = await response.Content.ReadFromJsonAsync<WorkflowDefinitionResponse[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(definitions!);
    }

    [Fact]
    public async Task Definitions_are_ordered_by_id_then_version()
    {
        // Stable ordering, so a deployment check diffs cleanly between runs.
        using var factory = new FlowDeckApiFactory()
            .With(new SimpleWorkflow("zulu", 2))
            .With(new SimpleWorkflow("alpha", 1))
            .With(new SimpleWorkflow("zulu", 1));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/workflows");
        var definitions = await response.Content.ReadFromJsonAsync<WorkflowDefinitionResponse[]>();

        Assert.Equal(
            [("alpha", 1), ("zulu", 1), ("zulu", 2)],
            definitions!.Select(d => (d.Id, d.Version)));
    }
}
