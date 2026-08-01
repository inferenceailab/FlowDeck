@M7
Feature: Workflow detail
  What a workflow does, before a run of it goes wrong. The shape is a tree of
  nested sequences, rendered as nested ordered lists rather than drawn on a
  canvas: a list is navigable by a screen reader and needs no dependency.

  @issue-172
  Scenario: A linear definition renders as an ordered sequence
    Given a definition with three sequential steps
    When I open its detail view
    Then the three steps are shown in order

  @issue-172
  Scenario: A choice renders its branches
    Given a definition whose step branches into "in-stock" and "backorder"
    When I open its detail view
    Then both branches are shown, labelled with their names
    And each branch shows the steps inside it

  @issue-172
  Scenario: A fork is distinguishable from a choice
    Given a definition with a fork and a definition with a choice
    When I open each detail view
    Then the fork states that every branch runs
    And the choice states that one branch is taken

  @issue-172
  Scenario: A retrying step shows its policy
    Given a definition whose step allows three attempts
    When I open its detail view
    Then that step shows it retries
    And a step that does not retry says nothing about attempts

  @issue-172
  Scenario: A compensated step is marked
    Given a definition whose step declares a compensating action
    When I open its detail view
    Then that step is marked as having an undo
    And a step with no compensating action is not
