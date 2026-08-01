@M9
Feature: Throughput baseline
  "Is it fast enough" was an opinion. These scenarios make it a number, and
  guard it loosely enough that a busy CI runner does not fail the build while
  an order-of-magnitude regression does.

  @issue-206
  Scenario: Instances per second is measured
    Given a definition with three steps
    When two hundred instances are run
    Then the measured rate is reported
    And it is above the floor a tenfold regression would breach

  @issue-206
  Scenario: Checkpoint cost per step is measured
    Given a one-step definition and a ten-step definition
    When fifty instances of each are run
    Then the per-step cost is reported
    And a ten-step instance costs less than ten times a one-step one

  @issue-206
  Scenario: Backlog recovery is measured
    Given fifty instances abandoned by a dead node
    When a dispatcher recovers them
    Then every one is recovered
    And the time to clear the backlog is reported
