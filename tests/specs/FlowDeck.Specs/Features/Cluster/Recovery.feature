@M6
Feature: Recovering abandoned work
  A node that dies leaves its instances behind. Every other node polls for work
  nobody is holding, claims it, and carries on from the last checkpoint.

  @issue-146
  Scenario Outline: An instance left Running by a crash becomes claimable
    Given the <provider> workflow store
    And an instance left Running with an expired lease
    When claimable work is queried
    Then that instance is offered

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |

  @issue-146
  Scenario Outline: An instance being actively worked is not offered
    Given the <provider> workflow store
    And an instance owned by a node whose lease is still live
    When claimable work is queried
    Then that instance is not offered

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |

  @issue-146
  Scenario Outline: A terminal instance is never offered
    Given the <provider> workflow store
    And a completed instance with an expired lease
    When claimable work is queried
    Then that instance is not offered

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |

  @issue-146
  Scenario: A completed step is not re-executed after recovery
    Given a crashed instance that had completed steps A and B
    When another node recovers it
    Then execution resumes at step C

  @issue-147
  Scenario: The dispatcher claims and runs a suspended instance
    Given a suspended instance and a dispatcher
    When the dispatcher polls
    Then the instance is resumed

  @issue-147
  Scenario: Two dispatchers do not run the same instance
    Given one claimable instance and two dispatchers
    When both dispatchers poll at the same moment
    Then exactly one of them runs it

  @issue-147
  Scenario: The dispatcher survives a failing instance
    Given a claimable instance whose step throws
    When the dispatcher polls twice
    Then the dispatcher is still polling

  @issue-147
  Scenario: The dispatcher releases the lease when it finishes
    Given a suspended instance and a dispatcher
    When the dispatcher polls
    Then the instance has no owner afterwards

  # A mutation removing the release passed this suite until the two scenarios
  # below existed. The lease looked released because the engine was silently
  # erasing it on every checkpoint, not because anyone released it.

  @issue-147
  Scenario: A checkpoint does not erase the lease
    Given an instance a node has claimed
    When the engine checkpoints it
    Then the node still owns it

  @issue-147
  Scenario: The dispatcher survives an instance it cannot run
    Given a claimable instance whose definition this node does not know
    When the dispatcher polls
    Then the dispatcher is still polling
    And that instance is left for another node

  @issue-149
  Scenario: A stopping node releases its leases
    Given a node holding a lease on an instance
    When the host stops gracefully
    Then the instance has no owner afterwards
    And another node can claim it immediately

  @issue-149
  Scenario: A killed node does not release
    Given a node holding a lease on an instance
    When the process dies without shutting down
    Then the lease is still held
    And it lapses on its own

  @issue-149
  Scenario: Draining does not release another node's lease
    Given a node holding a lease on an instance
    When a different node drains
    Then the lease is still held

  # Found when the readiness scenarios started failing: an unreachable store
  # took the dispatcher loop down with it, so a database blip would have left
  # every node permanently idle while still reporting itself alive.
  @issue-147
  Scenario: The dispatcher survives an unreachable store
    Given a dispatcher whose store is unreachable
    When the dispatcher polls repeatedly
    Then it records the failures and keeps polling
