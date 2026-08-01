@M5
Feature: Resilience over HTTP and in the guide
  The compensation statuses reach a client, and the limits retry and
  compensation carry are written where an author will find them.

  # Issue #122 has four scenarios. The two here are observable over HTTP; the
  # other two - "I open its detail view" - are Angular, and a .NET runner cannot
  # execute them. They are covered today by the Vitest specs and belong with the
  # other frontend scenarios in #135, not faked here against the API.

  @issue-122
  Scenario: The new statuses reach the API
    Given a Compensated instance exists
    When I read it over HTTP
    Then its status serialises as "Compensated"

  @issue-122
  Scenario: The list can be filtered to the new statuses
    Given instances in more than one terminal status
    When I filter by Compensated
    Then only compensated instances are listed

  @issue-108
  Scenario: The usage guide states the retry requirement
    Given the retry section of the usage guide
    Then it states that a retried step runs again in full
    And it states that the engine offers no duplicate protection
    And it shows an idempotency-key example

  @issue-108
  Scenario: The idempotency requirement is compiled
    Given a step deriving its idempotency key from the instance
    And a gateway that charges before timing out
    When the step is retried
    Then the card is charged exactly once

  @issue-123
  Scenario: The guide has a compensation section
    Given the compensation section of the usage guide
    Then it shows how to declare a compensating action
    And it states that rollback runs in reverse order
    And it states that rollback continues past a failing action
    And it states that compensation is best-effort

  @issue-123
  Scenario: The compensation example is compiled
    Given the guide's reserve, charge and ship workflow
    When shipping fails
    Then the refund runs before the stock release
