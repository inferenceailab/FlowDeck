@M1
Feature: Instance lifecycle
  Every execution is an instance with its own identity, timestamps and status.
  An operator can see where it is and stop it.

  @issue-7
  Scenario: Starting an instance returns a unique id
    Given a registered definition
    When two instances are started
    Then each returns a distinct non-empty instance id

  @issue-8
  Scenario: Timestamps are recorded
    Given an instance that runs to completion
    When I query the instance
    Then CreatedAt is set
    And CompletedAt is set
    And CompletedAt is greater than or equal to CreatedAt

  @issue-8
  Scenario: An incomplete instance has no completion time
    Given a suspended instance
    When I query the instance
    Then CompletedAt is null

  @issue-11
  Scenario: Status reflects the current step
    Given a running instance suspended at step B
    When I query the instance
    Then the status is Suspended
    And the current step name is "B"

  @issue-12
  Scenario: A suspended instance can be cancelled
    Given a suspended instance
    When I cancel it
    Then the instance status becomes Cancelled
    And no further steps execute

  @issue-12
  Scenario: A completed instance cannot be cancelled
    Given a completed instance
    When I cancel it
    Then the call fails with an InvalidStateTransitionException
