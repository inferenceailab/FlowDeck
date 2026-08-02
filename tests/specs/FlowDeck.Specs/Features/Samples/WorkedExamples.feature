@M12
Feature: FlowDeck runs locally with worked examples
  A clone starts up correct and completely blank, which reads as a broken
  deployment rather than a clean one. The samples exist so the first page load
  has something on it — and they are asserted here because nothing else runs
  them, so a builder change could break them silently and stay broken until
  somebody cloned the repository.

  @issue-235
  Scenario: The samples cover every shape the dashboard draws
    Given the sample definitions are registered
    Then one is a straight line, one forks, and one branches on a condition
    And at least one step declares a compensation
    And at least one step declares a retry policy

  @issue-235
  Scenario: A sample instance ends in each state an operator sees
    When every sample is run
    Then one instance completed, one compensated, and one suspended

  @issue-235
  Scenario: A completed order carries a retry in its history
    When every sample is run
    Then the order fulfilment history shows a failed attempt followed by a successful one

  @issue-235
  Scenario: A rollback undoes what the failed run had already done
    When every sample is run
    Then the reconciliation instance recorded its ledger step as undone

  @issue-235
  Scenario: Resuming a suspended review takes the conditional branch
    Given a suspended sample review
    When it is resumed
    Then it completes and its approval was recorded

  @issue-235
  Scenario: Samples stay out of a host that did not ask for them
    Given a host started without the samples flag
    When I GET /api/workflows
    Then no definition is registered
