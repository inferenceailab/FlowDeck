@M4 @M5
Feature: Instance detail
  The view an operator opens after an alert. Its job is answering where and why
  a run failed, and what the engine did about it.

  @issue-33
  Scenario: Timeline reflects execution history
    Given an instance that executed steps A, B and C
    When I open its detail view
    Then the timeline shows A, B and C in order with their outcomes

  @issue-33
  Scenario: The failing step is called out
    Given an instance that failed at step B
    When I open its detail view
    Then step B is marked as the failure point
    And the recorded error message is shown

  # From #122. The other two scenarios on that issue are observable over HTTP
  # and live in the backend specs; these two are view behaviour, so they belong
  # here rather than being approximated against the API.

  @issue-122
  Scenario: Compensating actions appear in the timeline
    Given an instance that rolled back two steps
    When I open its detail view
    Then the timeline shows both compensating actions, marked as rollback

  @issue-122
  Scenario: A partial rollback is called out
    Given a CompensationFailed instance
    When I open its detail view
    Then it states which compensating actions failed
