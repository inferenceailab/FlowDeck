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

  # Two actions rather than a flag (ADR-0028). The wording of each prompt is
  # what makes the difference legible, so it is asserted rather than assumed.

  @issue-124
  Scenario: Cancelling and rolling back is a separate action
    Given a suspended instance is displayed
    When I trigger the cancel and roll back action
    Then the prompt says work already done will be reversed
    And confirming calls the cancel-and-roll-back endpoint

  @issue-124
  Scenario: Plain cancel says work is not reversed
    Given a suspended instance is displayed
    When I trigger the cancel action
    Then the prompt says work already done is not reversed
