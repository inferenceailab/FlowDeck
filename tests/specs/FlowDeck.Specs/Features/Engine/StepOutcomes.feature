@M1
Feature: Step outcomes
  A step tells the engine what to do next by returning an Outcome. It may
  advance, park itself for later, or throw.

  # Issue #2 was written before ADR-0012 and says IStepBody and Outcome.Persist.
  # The names below are the ones that shipped; the assertions are unchanged.

  @issue-2
  Scenario: A step executes and reports success
    Given a step implementing IStep that returns Outcome.Next
    When the engine executes the step
    Then the step result is Success
    And the workflow advances past that step

  @issue-2
  Scenario: A step signals it is not yet complete
    Given a step returning Outcome.Suspend
    When the engine executes the step
    Then the instance remains at the same step
    And the instance status is Suspended

  @issue-6
  Scenario: Unhandled exception fails the instance
    Given a step that throws InvalidOperationException
    When the instance executes that step
    Then the instance status becomes Failed
    And the recorded error message contains "InvalidOperationException"
    And the failing step name is recorded
