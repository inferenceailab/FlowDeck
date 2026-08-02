@M10
Feature: Retrying from the step that failed
  Repeating work that already succeeded is not free — it may charge a card
  twice. An operator who knows the earlier steps are done wants to continue
  from the break, not from the beginning.

  @issue-217
  Scenario: The new instance begins at the failing step
    Given an instance that ran A and B and failed at C
    When I POST to its retry-from-failed-step endpoint
    Then the new instance runs C onward
    And A and B do not run again

  @issue-217
  Scenario: The workflow data the original had reached is carried over
    Given an instance whose earlier steps wrote to workflow data before it failed
    When I POST to its retry-from-failed-step endpoint
    Then the new instance sees what those steps wrote

  @issue-217
  Scenario: The original is left as it was and the new instance links to it
    Given an instance that ran A and B and failed at C
    When I POST to its retry-from-failed-step endpoint
    Then the original is still Failed
    And the new instance records which one it was retried from

  @issue-217
  Scenario: A failure inside a branch resumes inside that branch
    Given a forked instance that failed on a branch step
    When I POST to its retry-from-failed-step endpoint
    Then only the branch that failed runs again

  @issue-217
  Scenario: A rolled back instance is refused
    Given an instance that failed and was rolled back
    When I POST to its retry-from-failed-step endpoint
    Then the response status is 409

  @issue-217
  Scenario: An instance that failed on its first step retries from the beginning
    Given an instance that failed on its first step
    When I POST to its retry-from-failed-step endpoint
    Then the new instance runs from the beginning
