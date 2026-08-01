@M7
Feature: Concurrent branches
  A fork whose arms run one after another is a sequence written confusingly. The
  arms overlap in time, the join waits for all of them, and the checkpoints they
  produce go through one writer so they cannot reject each other.

  @issue-164
  Scenario: Parallel branches overlap in time
    Given a fork into two steps that each block until released
    When an instance is started
    Then both steps are running at the same moment

  @issue-164
  Scenario: A join waits for every branch
    Given a fork into two branches of different lengths
    When an instance is started
    Then the step after the join runs only once both branches have finished

  @issue-164
  Scenario: A failing branch fails the instance
    Given a fork where one branch throws and the other succeeds
    When an instance is started
    Then the other branch still runs to completion
    And the instance status becomes Failed

  @issue-164
  Scenario: Checkpoints from concurrent branches do not collide
    Given a fork into two branches that each complete several steps
    When an instance is started
    Then every checkpoint is written
    And no concurrency exception is raised

  @issue-164
  Scenario: Concurrent writes to workflow data are not lost
    Given a fork into two branches that each write a different key
    When an instance is started
    Then both values are readable after the join

  @issue-164
  Scenario: Concurrent writes do not corrupt the data bag
    Given a fork into two branches that each write hundreds of keys
    When an instance is started
    Then every key written by either branch is readable

  @issue-164
  Scenario: A forked instance is durably at every branch at once
    Given a fork into two steps that each block until released
    When an instance is started
    Then the stored position names both branch steps
    And each names the branch it belongs to

  @issue-164
  Scenario: A conditional branch runs only when its condition holds
    Given a step with two conditional branches and data selecting the second
    When an instance is started
    Then only the second branch runs

  @issue-164
  Scenario: A choice with no matching condition continues past the fork
    Given a step with two conditional branches and data selecting neither
    When an instance is started
    Then no branch runs
    And the step after the branches still runs
