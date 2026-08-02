@M8
Feature: Retry and compensation counters
  M5 built both mechanisms and neither was measurable. An operator with only
  Prometheus could see that instances fail; not that one step is retrying on
  every run, or that half the undos are failing.

  @issue-199
  Scenario: Only attempts beyond the first are counted
    Given a step that fails twice and then succeeds
    When an instance is started
    Then the retry counter reports two
    And a workflow that never retried reports nothing

  @issue-199
  Scenario: Retries are counted per step
    Given two steps that each retry once
    When an instance is started
    Then each step's retries are counted under its own name

  @issue-199
  Scenario: Each compensating action is counted
    Given an instance whose rollback undoes two steps
    When it fails
    Then two compensating actions are counted, both as undone

  @issue-199
  Scenario: A failed undo is counted separately from a successful one
    Given an instance where one compensating action throws and one succeeds
    When it fails
    Then one action is counted as undone and one as failed

  @issue-199
  Scenario: The counters carry no workflow data
    Given a definition whose step writes a secret and then retries
    When an instance is started
    Then no tag on any measurement contains that secret
