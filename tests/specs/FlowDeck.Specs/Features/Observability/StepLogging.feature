@M8
Feature: Step execution logging
  Lifecycle entries say an instance is running. They do not say what it is
  running, which is the question an operator has while a workflow is slow,
  retrying, or unwinding — and the one history cannot answer, because history is
  written after each step rather than during it.

  @issue-186
  Scenario: A step logs its start and its outcome
    Given a definition with steps "reserve" and "charge"
    When an instance is started
    Then each step logs that it started and how it finished
    And the outcome entry carries how long the step took

  @issue-186
  Scenario: A retry is distinguishable from a first attempt
    Given a step that fails twice and then succeeds
    When an instance is started
    Then each attempt is logged with its own attempt number
    And an entry between the attempts says a retry is scheduled, with the delay

  @issue-186
  Scenario: A rollback is not logged as ordinary progress
    Given an instance whose rollback undoes two steps
    When it fails
    Then each compensating action is logged as a rollback rather than as a step
    And the rolled back step names are the ones that ran

  @issue-186
  Scenario: A compensating action that fails is logged as an error
    Given an instance whose compensating action throws
    When it fails
    Then the failed rollback is logged as an error naming the step

  @issue-186
  Scenario: A step inside a branch logs which branch it ran on
    Given a definition that forks into steps "left" and "right"
    When an instance is started
    Then each branch step's entries carry the branch it ran on
    And a step on the top-level sequence carries no branch
