@M4
Feature: Instance list
  The view an operator opens first. It has to say what is running, what broke,
  and what is happening right now — including while it is still loading.

  @issue-32
  Scenario: Instances are listed with status
    Given the API returns three instances
    When I open the instances view
    Then three rows are rendered
    And each row shows id, definition, status and start time

  @issue-32
  Scenario: Failed instances are visually distinct
    Given an instance with status Failed
    When I open the instances view
    Then that row carries the failure styling

  @issue-34
  Scenario: Loading state is shown while fetching
    Given the instances request has not resolved
    When I open the instances view
    Then a loading indicator is visible

  @issue-34
  Scenario: Empty state is shown when there are no instances
    Given the API returns an empty list
    When I open the instances view
    Then an empty state message is shown instead of an empty table

  @issue-34
  Scenario: Error state is shown when the API fails
    Given the API returns 500
    When I open the instances view
    Then an error message with a retry action is shown

  @issue-36
  Scenario: Status changes appear without a page reload
    Given the instances view is open
    When an instance transitions from Running to Completed
    Then the displayed status updates within the refresh interval
