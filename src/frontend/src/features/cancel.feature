@M4
Feature: Cancelling from the dashboard
  Cancelling is irreversible, so it takes two deliberate actions — and the
  control is never offered for an instance the API would refuse.

  @issue-35
  Scenario: Cancel action calls the API and refreshes
    Given a suspended instance is displayed
    When I trigger the cancel action and confirm
    Then POST to the cancel endpoint is called
    And the row status updates to Cancelled

  @issue-35
  Scenario: Cancel is unavailable for completed instances
    Given a completed instance is displayed
    Then the cancel action is disabled
