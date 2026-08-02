@M10
Feature: Suspending a running instance
  Parking work without cancelling it. The engine cannot interrupt a step
  mid-execution, so this takes effect at the next step boundary — and says so
  rather than pretending otherwise.

  @issue-218
  Scenario: The step in flight finishes and the next one does not start
    Given a running instance blocked inside its first step
    When it is suspended and the step is released
    Then the first step finished
    And the second step never started
    And the instance is Suspended

  @issue-218
  Scenario: A suspended instance can be resumed and carries on
    Given a running instance blocked inside its first step
    When it is suspended and the step is released
    And it is resumed
    Then the second step runs

  @issue-218
  Scenario: The request does not survive as a standing order
    Given a running instance blocked inside its first step
    When it is suspended and the step is released
    And it is resumed
    Then it runs to completion rather than parking again
    And the stored request is cleared

  @issue-218
  Scenario: Suspending a terminal instance is refused
    Given a completed instance
    When it is suspended
    Then the call fails saying it has already finished

  @issue-218
  Scenario: Suspending an already suspended instance is refused
    Given an instance that has parked on its own
    When it is suspended
    Then the call fails saying it has already finished
