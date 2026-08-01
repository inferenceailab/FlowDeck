@M4 @M5 @M6
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

  @issue-148
  Scenario: The detail view shows the owning node
    Given an instance owned by "node-a"
    When I open its detail view
    Then it shows that node-a is running it

  @issue-148
  Scenario: An unowned instance shows no node
    Given a completed instance with no owner
    When I open its detail view
    Then no owning node is shown

  @issue-148
  Scenario: An expired lease is called out
    Given a Running instance the API reports as awaiting recovery
    When I open its detail view
    Then it states the instance is awaiting recovery
