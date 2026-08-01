@M7
Feature: A set-valued position
  An instance forked into three branches is at three places at once. The durable
  position becomes a set of active nodes, and the single index survives only as
  a projection for workflows that are still a straight line.

  @issue-163
  Scenario Outline: Active nodes round-trip through the store
    Given the <provider> workflow store
    When an instance is saved with three active nodes
    Then reading it back returns all three

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |

  @issue-163
  Scenario Outline: An empty active set round-trips as empty
    Given the <provider> workflow store
    When an instance with active nodes has them cleared
    Then reading it back returns no active nodes

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |

  @issue-163
  Scenario: A linear instance still reports a current step index
    Given a linear workflow paused at its second step
    Then CurrentStepIndex is 1
    And the active node set contains exactly that step

  @issue-163
  Scenario: A retrying step carries its attempt count into the set
    Given a linear workflow whose second step has failed twice and will retry
    Then the active node reports two attempts

  @issue-163
  Scenario: A finished instance is active nowhere
    Given a linear workflow that runs to completion
    Then the active node set is empty

  @issue-163
  Scenario: A failed instance is active nowhere either
    Given a linear workflow whose step fails without a retry policy
    Then the active node set is empty
    But the failed step is still named

  @issue-163
  Scenario: Attempts are counted per node
    Given an instance active at two nodes
    When one node has failed twice and the other once
    Then each node reports its own attempt count

  @issue-163
  Scenario: Node order is not significant but is stable
    Given an instance saved with three active nodes
    When it is read back twice
    Then the nodes come back in the same order both times
