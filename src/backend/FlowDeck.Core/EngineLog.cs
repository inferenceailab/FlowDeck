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
}
