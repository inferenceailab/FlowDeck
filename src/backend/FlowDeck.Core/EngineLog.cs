using Microsoft.Extensions.Logging;

namespace FlowDeck.Core;

/// <summary>
/// Everything the engine says about an instance's life.
/// </summary>
/// <remarks>
/// Source-generated with <c>[LoggerMessage]</c> rather than written as
/// <c>logger.LogInformation(...)</c> calls. Three reasons, in order of how much
/// they matter here:
///
/// <para>
/// <b>Cost.</b> This is the engine's hot path. A generated method checks
/// <c>IsEnabled</c> before doing any work, so a host with logging off pays a
/// branch rather than boxing every argument into an <c>object[]</c>.
/// </para>
///
/// <para>
/// <b>Identity.</b> Each event gets a stable <c>EventId</c>, so an operator's
/// alert and this project's tests can both key on the event rather than on
/// prose that may be reworded.
/// </para>
///
/// <para>
/// <b>Discipline.</b> The signatures are the whole vocabulary of what the engine
/// may say, in one file. ADR-0025 decision 3 forbids workflow data reaching any
/// signal, and a rule like that is far easier to keep when every field the
/// engine can emit is declared in one place a reviewer can read.
/// </para>
/// </remarks>
internal static partial class EngineLog
{
    [LoggerMessage(
        EventId = 1000,
        EventName = "InstanceStarted",
        Level = LogLevel.Information,
        Message = "Instance of {DefinitionId} v{DefinitionVersion} started.")]
    public static partial void InstanceStarted(
        this ILogger logger,
        string definitionId,
        int definitionVersion);

    [LoggerMessage(
        EventId = 1001,
        EventName = "InstanceCompleted",
        Level = LogLevel.Information,
        Message = "Instance of {DefinitionId} completed in {ElapsedMs}ms.")]
    public static partial void InstanceCompleted(
        this ILogger logger,
        string definitionId,
        double elapsedMs);

    /// <summary>
    /// The one entry an operator is most likely to be woken by.
    /// </summary>
    /// <remarks>
    /// <b>Error</b>, where progress is Information: a level that does not
    /// separate the two makes every alerting rule a text match.
    ///
    /// <para>
    /// The exception type and message, never the exception itself. The engine
    /// already keeps the unwrapped exception on the instance for NFR-2; passing
    /// it here would put an author's stack trace - and whatever their exception
    /// message interpolated - into a log sink that may be exported. What crossed
    /// the API boundary in M3 is the type and the message, and this matches it.
    /// </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 1002,
        EventName = "InstanceFailed",
        Level = LogLevel.Error,
        Message = "Instance of {DefinitionId} failed at step {FailedStepName}: {ErrorType}: {ErrorMessage}")]
    public static partial void InstanceFailed(
        this ILogger logger,
        string definitionId,
        string? failedStepName,
        string? errorType,
        string? errorMessage);

    [LoggerMessage(
        EventId = 1003,
        EventName = "InstanceCancelled",
        Level = LogLevel.Information,
        Message = "Instance of {DefinitionId} cancelled at step {CurrentStepName}.")]
    public static partial void InstanceCancelled(
        this ILogger logger,
        string definitionId,
        string? currentStepName);

    [LoggerMessage(
        EventId = 1004,
        EventName = "InstanceSuspended",
        Level = LogLevel.Information,
        Message = "Instance of {DefinitionId} suspended at step {CurrentStepName}.")]
    public static partial void InstanceSuspended(
        this ILogger logger,
        string definitionId,
        string? currentStepName);

    [LoggerMessage(
        EventId = 1005,
        EventName = "InstanceResumed",
        Level = LogLevel.Information,
        Message = "Instance of {DefinitionId} resumed at step {CurrentStepName}.")]
    public static partial void InstanceResumed(
        this ILogger logger,
        string definitionId,
        string? currentStepName);

    /// <summary>
    /// A rollback finished, whether or not it managed everything.
    /// </summary>
    /// <remarks>
    /// <b>Warning</b>, not Error. The original failure has already been logged
    /// as an error, and repeating that severity for the cleanup would double
    /// every incident. <c>CompensationFailed</c> is the case that needs a human
    /// (ADR-0021) and it is distinguishable by <c>Status</c> rather than by
    /// having its own event, so an operator alerts on one field.
    /// </remarks>
    [LoggerMessage(
        EventId = 1006,
        EventName = "InstanceCompensated",
        Level = LogLevel.Warning,
        Message = "Instance of {DefinitionId} rolled back and settled as {Status}.")]
    public static partial void InstanceCompensated(
        this ILogger logger,
        string definitionId,
        InstanceStatus status);

    /// <summary>
    /// A step is about to run.
    /// </summary>
    /// <remarks>
    /// <b>Debug</b>, unlike the lifecycle events. A workflow of twenty steps
    /// would otherwise emit forty Information entries per instance and bury the
    /// six that describe the run as a whole.
    ///
    /// <para>
    /// The default therefore is: quiet while a workflow is healthy, and loud
    /// the moment it retries, rolls back or fails - each of which has its own
    /// event above Debug. An operator who wants the play-by-play turns
    /// FlowDeck.Core down to Debug and gets it.
    /// </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 1100,
        EventName = "StepStarted",
        Level = LogLevel.Debug,
        Message = "Step {StepName} started, attempt {Attempt}.")]
    public static partial void StepStarted(
        this ILogger logger,
        string stepName,
        int attempt);

    [LoggerMessage(
        EventId = 1101,
        EventName = "StepFinished",
        Level = LogLevel.Debug,
        Message = "Step {StepName} finished as {Status} in {ElapsedMs}ms, attempt {Attempt}.")]
    public static partial void StepFinished(
        this ILogger logger,
        string stepName,
        StepStatus status,
        double elapsedMs,
        int attempt);

    /// <summary>
    /// A step failed and will be attempted again.
    /// </summary>
    /// <remarks>
    /// <b>Warning</b>, and it carries the delay. A workflow backing off for
    /// thirty seconds and a workflow that has hung look identical from outside,
    /// and this entry is the difference between the two.
    /// </remarks>
    [LoggerMessage(
        EventId = 1102,
        EventName = "StepRetrying",
        Level = LogLevel.Warning,
        Message = "Step {StepName} failed on attempt {Attempt} ({ErrorType}); retrying in {DelayMs}ms.")]
    public static partial void StepRetrying(
        this ILogger logger,
        string stepName,
        int attempt,
        string? errorType,
        double delayMs);

    /// <summary>
    /// A compensating action undid a step.
    /// </summary>
    /// <remarks>
    /// Its own event rather than a <see cref="StepFinished"/> with a
    /// <c>compensate:</c> name. A rollback is not progress, and an operator
    /// filtering on the forward events should not have to know the engine's
    /// history naming convention (ADR-0021) to exclude it.
    ///
    /// <para>
    /// The name here is the step being undone, without that prefix. The prefix
    /// is how history keeps two entries apart in one table; it is not something
    /// to make a reader parse.
    /// </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 1103,
        EventName = "StepRolledBack",
        Level = LogLevel.Information,
        Message = "Rolled back step {StepName}.")]
    public static partial void StepRolledBack(this ILogger logger, string stepName);

    /// <summary>
    /// A compensating action failed, so its step's effects are still in place.
    /// </summary>
    /// <remarks>
    /// <b>Error</b>, and one per failed action rather than one summary at the
    /// end. Rollback continues past a failure (ADR-0021), so an instance can
    /// leave several steps un-undone, and which ones is the whole content of
    /// the operator's next hour.
    /// </remarks>
    [LoggerMessage(
        EventId = 1104,
        EventName = "RollbackFailed",
        Level = LogLevel.Error,
        Message = "Could not roll back step {StepName}: {ErrorType}: {ErrorMessage}")]
    public static partial void RollbackFailed(
        this ILogger logger,
        string stepName,
        string? errorType,
        string? errorMessage);

    /// <summary>
    /// A definition version was removed from the registry.
    /// </summary>
    /// <remarks>
    /// Information, and it carries how many instances ever ran it. Retirement is
    /// rare, deliberate and irreversible without a redeploy, so the entry an
    /// operator will go looking for afterwards should say what it affected.
    /// </remarks>
    [LoggerMessage(
        EventId = 1007,
        EventName = "DefinitionRetired",
        Level = LogLevel.Information,
        Message = "Definition {DefinitionId} v{Version} retired; {InstancesEverRun} instance(s) had run it.")]
    public static partial void DefinitionRetired(
        this ILogger logger,
        string definitionId,
        int version,
        int instancesEverRun);

    /// <summary>
    /// An operator asked a running instance to park.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InstanceSuspended"/>, which says it actually
    /// has. Between the two an instance is still executing the step it was on,
    /// and an operator reading only the second would think their request had
    /// been ignored.
    /// </remarks>
    [LoggerMessage(
        EventId = 1008,
        EventName = "InstanceSuspendRequested",
        Level = LogLevel.Information,
        Message = "Instance of {DefinitionId} asked to suspend at step {CurrentStepName}; "
            + "it will park at the next step boundary.")]
    public static partial void InstanceSuspendRequested(
        this ILogger logger,
        string definitionId,
        string? currentStepName);
}
