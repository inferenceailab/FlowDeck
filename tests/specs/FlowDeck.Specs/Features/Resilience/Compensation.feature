@M5
Feature: Compensation
  When a workflow fails, the steps that already succeeded are undone in reverse
  order. Best-effort: the engine tries everything and reports honestly.

  @issue-118
  Scenario: A compensating action is declared beside its step
    Given a workflow declaring a step with WithCompensation
    When the definition is compiled
    Then that step carries its compensating action

  @issue-118
  Scenario: WithCompensation applies to the preceding step
    Given a workflow declaring two steps, the first with a compensating action
    When the definition is compiled
    Then only the first step carries one

  @issue-118
  Scenario: Declaring compensation before any step is rejected
    Given a workflow calling WithCompensation before AddStep
    When a compensating instance is started
    Then InvalidWorkflowDefinitionException is raised

  @issue-119
  Scenario: A failure rolls back completed steps
    Given a workflow whose first step has a compensating action
    And whose second step throws
    When a compensating instance is started
    Then the compensating action runs

  @issue-119
  Scenario: Rollback runs in reverse execution order
    Given three steps with compensating actions
    And the third step throws
    When a compensating instance is started
    Then the compensating actions run in reverse declaration order

  @issue-119
  Scenario: A step without a compensating action is skipped
    Given a workflow where only the second of three steps declares one
    And the third step throws
    When a compensating instance is started
    Then only that action runs, and the others are not treated as failures

  @issue-119
  Scenario: The failing step itself is compensated
    Given a step that exhausted its retries and declares a compensating action
    When a compensating instance is started
    Then that action runs exactly once

  @issue-120
  Scenario: A fully compensated instance reports Compensated
    Given a workflow whose first step has a compensating action
    And whose second step throws
    When a compensating instance is started
    Then the compensating instance status is Compensated

  @issue-120
  Scenario: A failed compensating action reports CompensationFailed
    Given a workflow that fails and one compensating action that throws
    When a compensating instance is started
    Then the compensating instance status is CompensationFailed

  @issue-120
  Scenario: A failure with no compensating actions still reports Failed
    Given a workflow with no compensating actions whose step throws
    When a compensating instance is started
    Then the compensating instance status is Failed

  @issue-120
  Scenario: The new statuses are terminal
    Given a Compensated instance
    When it is cancelled
    Then InvalidStateTransitionException is raised for the rollback

  @issue-121
  Scenario: Rollback continues past a failure
    Given three steps with compensating actions
    And the second action to run throws
    And the third step throws
    When a compensating instance is started
    Then all three actions are attempted

  @issue-121
  Scenario: Every failure is recorded
    Given two compensating actions that both throw
    When a compensating instance is started
    Then history records both, each with its own error

  @issue-121
  Scenario: The original failure is not overwritten
    Given a workflow whose step failed with "card declined"
    And whose compensating action fails with something else
    When a compensating instance is started
    Then the instance still reports the original failure
