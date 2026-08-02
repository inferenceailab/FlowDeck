@M10
Feature: Retrying a finished instance
  The action an operator wants at 2am. It starts a new instance and leaves the
  original exactly as it was, because "this instance failed" is a fact and the
  record is what they are using to decide what to do.

  @issue-216
  Scenario: Retrying a failed instance starts a new one
    Given a failed instance of "orders" v1
    When I POST to its retry endpoint
    Then the response status is 202
    And a different instance id comes back
    And the new instance records which one it was retried from

  @issue-216
  Scenario: The original is left exactly as it was
    Given a failed instance of "orders" v1
    When I POST to its retry endpoint
    Then the original is still Failed
    And the original's history is unchanged

  @issue-216
  Scenario: The retry runs the version the original ran
    Given a failed instance of "orders" v1 and a registered v2
    When I POST to its retry endpoint
    Then the new instance runs v1

  @issue-216
  Scenario: The retry runs from the beginning with the original's input
    Given a failed instance started with input
    When I POST to its retry endpoint
    Then every step ran again
    And the new instance received the same input

  @issue-216
  Scenario: Retrying an instance that has not finished is refused
    Given a suspended instance
    When I POST to its retry endpoint
    Then the response status is 409
