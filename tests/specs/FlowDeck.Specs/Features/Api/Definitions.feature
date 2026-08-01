@M7
Feature: A definition's shape over HTTP
  Listing definitions says a workflow exists. This says what it does: the steps
  it declares, in order, and the branches that leave them. A condition is a
  compiled delegate, so a branch reports that it is conditional and never what
  the condition is.

  @issue-171
  Scenario: A definition reports its steps in order
    Given a registered definition with three sequential steps
    When I GET that definition over HTTP
    Then the steps are returned in declaration order

  @issue-171
  Scenario: A step reports its retry policy and whether it compensates
    Given a definition whose step retries three times and declares a compensating action
    When I GET that definition over HTTP
    Then that step reports three attempts and that it is compensated

  @issue-171
  Scenario: Branches are reported with their names
    Given a definition whose step declares branches "in-stock" and "backorder"
    When I GET that definition over HTTP
    Then both branches are returned with their steps
    And neither is parallel

  @issue-171
  Scenario: A predicate branch is reported as conditional
    Given a definition declaring a branch on a condition beside one the step selects
    When I GET that definition over HTTP
    Then that branch is marked conditional
    And the step-decided branch is not
    And no condition is described

  @issue-171
  Scenario: Fork branches are reported as parallel
    Given a definition forking into two branches
    When I GET that definition over HTTP
    Then both branches are marked parallel

  @issue-171
  Scenario: An unknown definition returns 404
    When I GET a definition id that is not registered
    Then the response status is 404
    And the body reports that the definition was not found
