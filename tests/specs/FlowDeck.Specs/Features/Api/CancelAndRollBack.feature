@M10
Feature: Cancelling and rolling back
  Two actions, not a flag. An operator stopping a workflow to fix forward would
  be destroyed by an automatic rollback; one abandoning work wants exactly that.

  @issue-124
  Scenario: Cancelling and rolling back undoes completed steps
    Given a suspended instance whose earlier steps declare compensating actions
    When I POST to its cancel-and-roll-back endpoint
    Then the compensating actions ran, most recently completed first
    And the instance reports Compensated

  @issue-124
  Scenario: Plain cancel still does not roll anything back
    Given a suspended instance whose earlier steps declare compensating actions
    When I POST to its cancel endpoint
    Then no compensating action ran
    And the instance reports Cancelled

  @issue-124
  Scenario: Rolling back a workflow with nothing to undo reports Cancelled
    Given a suspended instance whose steps declare no compensating actions
    When I POST to its cancel-and-roll-back endpoint
    Then the instance reports Cancelled

  @issue-124
  Scenario: A failing compensating action is reported
    Given a suspended instance whose compensating action throws
    When I POST to its cancel-and-roll-back endpoint
    Then the instance reports CompensationFailed

  @issue-124
  Scenario: Cancelling and rolling back a terminal instance is refused
    Given a completed instance
    When I POST to its cancel-and-roll-back endpoint
    Then the response status is 409
