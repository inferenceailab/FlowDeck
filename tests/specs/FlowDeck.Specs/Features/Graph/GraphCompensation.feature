@M7
Feature: Compensating a graph
  Reverse execution order stops being well defined once two branches ran at the
  same time. Compensation walks what actually happened, most recently completed
  first, so branching does not quietly cost an author the guarantee compensation
  gave them.

  @issue-165
  Scenario: A failure unwinds work done on sibling branches
    Given a fork where one branch throws and the other completed a compensated step
    When the instance fails
    Then the sibling branch's compensating action runs

  @issue-165
  Scenario: Compensation follows completion order, most recent first
    Given three compensated steps that completed in a known order
    When the instance fails
    Then their compensating actions run in the reverse of that order

  @issue-165
  Scenario: A step on an untaken branch is never compensated
    Given a conditional workflow that took the in-stock branch
    When the instance fails afterwards
    Then no step on the backorder branch is compensated

  @issue-165
  Scenario: A step retried several times is compensated once
    Given a compensated step that failed twice before succeeding
    When the instance fails afterwards
    Then its compensating action runs exactly once

  @issue-165
  Scenario: A failing compensating action does not stop the rollback
    Given a fork whose branches both declare compensation and one undo throws
    When the instance fails
    Then the other branch is still compensated
    And the instance status becomes CompensationFailed
