@M10
Feature: Resuming from the dashboard
  A suspended workflow was only continuable by code inside the process that
  started it. The operator who needs to continue one is looking at the
  dashboard.

  @issue-68
  Scenario: A suspended instance offers resume
    Given a suspended instance is displayed
    When I look at the actions
    Then resume is offered

  @issue-68
  Scenario: Resume is disabled for an instance that has not parked
    Given a running instance is displayed
    When I look at the actions
    Then resume is present but disabled

  @issue-68
  Scenario: Resuming asks the API and reloads
    Given a suspended instance is displayed
    When I resume it
    Then the resume endpoint is called
    And the instance is reloaded afterwards

  @issue-68
  Scenario: A refused resume is reported without losing the instance
    Given a suspended instance the API will refuse to resume
    When I resume it
    Then the refusal is shown
    And the instance is still on screen
