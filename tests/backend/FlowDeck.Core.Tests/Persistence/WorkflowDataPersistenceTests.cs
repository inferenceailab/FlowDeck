using FlowDeck.Core;
using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Issue #15 - Persist workflow data alongside instance state.
///
/// Scenario: Context survives a restart
/// </summary>
public class WorkflowDataPersistenceTests
{
    private sealed class WritesThenSuspends(HashSet<Guid> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            lock (seen)
            {
                if (seen.Add(context.InstanceId))
                {
                    context.Data.Set("orderId", 42);
                    return ValueTask.FromResult(Outcome.Suspend);
                }
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class ReadsOrderId(List<int> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(context.Data.Get<int>("orderId"));
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class TwoStep(Func<IStep> a, Func<IStep> b) : IWorkflowDefinition
    {
        public string Id => "data-restart";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", a);
            builder.AddStep("B", b);
        }
    }

    private static WorkflowEngine NewHost(IWorkflowStore store, IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry, store: store);
    }

    [Fact]
    public async Task Context_survives_a_restart()
    {
        // Given step A wrote "orderId" = 42 before suspension
        var store = new InMemoryWorkflowStore(new WorkflowDataSerializer());
        var read = new List<int>();
        var seen = new HashSet<Guid>();

        IWorkflowDefinition Definition() => new TwoStep(
            () => new WritesThenSuspends(seen),
            () => new ReadsOrderId(read));

        var started = await NewHost(store, Definition()).StartAsync("data-restart", 1);
        Assert.Equal(InstanceStatus.Suspended, started.Status);

        // When the instance resumes after a restart
        var resumed = await NewHost(store, Definition()).ResumeAsync(started.Id);

        // Then step B reads "orderId" as 42
        Assert.Equal([42], read);
        Assert.Equal(InstanceStatus.Completed, resumed.Status);
    }

    [Fact]
    public async Task Typed_input_survives_a_restart()
    {
        // Input is instance state, not workflow data (ADR-0006), so it needs
        // its own proof. A resumed step seeing null input would be a silent
        // data-loss bug, not a crash.
        var store = new InMemoryWorkflowStore(new WorkflowDataSerializer(
            new WorkflowDataSerializerOptions()));
        var read = new List<int>();
        var seen = new HashSet<Guid>();

        IWorkflowDefinition Definition() => new TypedWorkflow(seen, read);

        var started = await NewHost(store, Definition()).StartAsync("typed-restart", 1, new Order(7));
        Assert.Equal(InstanceStatus.Suspended, started.Status);

        await NewHost(store, Definition()).ResumeAsync(started.Id);

        Assert.Equal([7], read);
    }

    public sealed record Order(int Id);

    private sealed class TypedWorkflow(HashSet<Guid> seen, List<int> read) : IWorkflowDefinition<Order>
    {
        public string Id => "typed-restart";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", () => new SuspendOnce(seen));
            builder.AddStep("B", () => new ReadsInput(read));
        }
    }

    private sealed class SuspendOnce(HashSet<Guid> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            lock (seen)
            {
                return ValueTask.FromResult(seen.Add(context.InstanceId) ? Outcome.Suspend : Outcome.Next);
            }
        }
    }

    private sealed class ReadsInput(List<int> read) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            read.Add(context.GetInput<Order>().Id);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    // ------------------------------------------------------- serialisation

    [Fact]
    public void Common_value_types_round_trip_with_their_types_intact()
    {
        // Without a type tag, 42 and "42" are indistinguishable coming back in.
        var serializer = new WorkflowDataSerializer();
        var original = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["int"] = 42,
            ["string"] = "acme",
            ["bool"] = true,
            ["decimal"] = 12.34m,
            ["guid"] = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
            ["when"] = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
        };

        var restored = serializer.Deserialize(serializer.Serialize(original));

        Assert.Equal(42, restored["int"]);
        Assert.IsType<int>(restored["int"]);
        Assert.Equal("acme", restored["string"]);
        Assert.True((bool)restored["bool"]!);
        Assert.Equal(12.34m, restored["decimal"]);
        Assert.Equal(original["guid"], restored["guid"]);
        Assert.Equal(original["when"], restored["when"]);
    }

    [Fact]
    public void A_null_value_round_trips_as_present()
    {
        var serializer = new WorkflowDataSerializer();
        var original = new Dictionary<string, object?>(StringComparer.Ordinal) { ["note"] = null };

        var restored = serializer.Deserialize(serializer.Serialize(original));

        Assert.True(restored.ContainsKey("note"));
        Assert.Null(restored["note"]);
    }

    [Fact]
    public void An_unregistered_type_is_refused_at_write_time()
    {
        // Fails when the value is stored, naming the key - not later, when
        // something tries to read it back and cannot.
        var serializer = new WorkflowDataSerializer();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["order"] = new Order(7),
        };

        var ex = Assert.Throws<WorkflowDataSerializationException>(() => serializer.Serialize(data));

        Assert.Equal("order", ex.Key);
        Assert.Equal("Order", ex.TypeName);
    }

    [Fact]
    public void A_registered_type_round_trips()
    {
        var serializer = new WorkflowDataSerializer(new WorkflowDataSerializerOptions().Allow<Order>());
        var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = new Order(7) };

        var restored = serializer.Deserialize(serializer.Serialize(data));

        Assert.Equal(new Order(7), restored["order"]);
    }

    [Fact]
    public void A_type_name_that_is_not_allowed_is_never_resolved_on_read()
    {
        // The security property: whoever can write to the store must not be
        // able to choose which type gets constructed on read.
        var permissive = new WorkflowDataSerializer(new WorkflowDataSerializerOptions().Allow<Order>());
        var stored = permissive.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = new Order(7) });

        var strict = new WorkflowDataSerializer();

        var ex = Assert.Throws<WorkflowDataSerializationException>(() => strict.Deserialize(stored));

        Assert.Equal("order", ex.Key);
    }

    [Fact]
    public async Task An_unserialisable_value_fails_in_the_test_suite_not_only_in_production()
    {
        // The trap this design exists to close: a workflow that works against
        // a plain in-memory store and breaks against a real provider. Running
        // the double through the serialiser surfaces it here.
        var store = new InMemoryWorkflowStore(new WorkflowDataSerializer());
        var registry = new WorkflowRegistry();
        registry.Register(new StoresUnserialisable());
        var engine = new WorkflowEngine(registry, store: store);

        await Assert.ThrowsAsync<WorkflowDataSerializationException>(
            async () => await engine.StartAsync("unserialisable", 1));
    }

    private sealed class StoresUnserialisable : IWorkflowDefinition
    {
        public string Id => "unserialisable";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new StoresAnOrder());
    }

    private sealed class StoresAnOrder : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            context.Data.Set("order", new Order(7));
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
