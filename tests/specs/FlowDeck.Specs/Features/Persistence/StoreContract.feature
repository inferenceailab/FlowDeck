@M2
Feature: The workflow store contract
  Every persistence provider satisfies the same contract, so a host can change
  database without changing how FlowDeck behaves.

  # Issues #16 and #17 word these as "every conformance test passes". Asserted
  # literally, from inside the same test run, that would be a suite reporting on
  # itself - true whenever it is green and worthless when it is not.
  #
  # Executed instead against each provider in turn, which is what the scenario
  # was reaching for. The exhaustive contract remains
  # WorkflowStoreConformanceTests in the unit suite; these demonstrate that both
  # providers are held to one contract rather than to their own.

  @issue-16 @issue-17
  Scenario Outline: A provider round-trips instance state
    Given the <provider> workflow store
    When an instance is created and then saved with new state
    Then reading it back returns the saved state
    And the revision has advanced

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |

  @issue-16 @issue-17
  Scenario Outline: A provider appends history without rewriting it
    Given the <provider> workflow store
    When two batches of history are appended
    Then the entries are returned in execution order
    And the earlier entries are unchanged

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |

  @issue-18
  Scenario: Each step execution appends a history entry
    Given a three step workflow that completes
    When I read the instance history
    Then there are three entries in execution order
    And each entry records step name, start time, end time and outcome

  @issue-18
  Scenario: History is never mutated
    Given an instance with existing history
    When the instance executes a further step
    Then earlier history entries are unchanged

  @issue-19
  Scenario: Stale write is rejected
    Given an instance loaded at its current revision
    And another writer has since saved a newer revision
    When the first writer saves
    Then a WorkflowStoreConcurrencyException is raised
    And the stored state remains at the newer revision

  @issue-21
  Scenario: Migration is idempotent
    Given a store already at the current schema version
    When migrations are applied again
    Then no changes are made and no error is raised
