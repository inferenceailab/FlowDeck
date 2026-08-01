@M8
Feature: Instance outcome counters
  Logs answer what happened to one instance. "How often does this workflow
  fail" is a different question, and answering it from logs needs an
  aggregation pipeline a homelab does not have.

  @issue-187
  Scenario: Starting an instance increments the started counter
    Given a definition "order-fulfilment" version 3 that completes
    When three instances are started
    Then the started counter reports three
    And every measurement is tagged with the definition id and version

  @issue-187
  Scenario: Each terminal outcome increments its own counter
    Given a definition that completes, one that fails and one that is cancelled
    When each reaches its terminal state
    Then each outcome counter reports one
    And no counter reports an outcome that did not happen

  @issue-187
  Scenario: A rolled back instance is counted as compensated, not as failed
    Given a definition whose rollback undoes a step
    When an instance fails
    Then the compensated counter reports one
    And the failed counter reports nothing

  @issue-187
  Scenario: A partially rolled back instance is counted as such
    Given a definition whose compensating action throws
    When an instance fails
    Then the compensated counter reports one, tagged as a failed rollback

  @issue-187
  Scenario: Counters carry no workflow data
    Given a definition whose step writes a secret into workflow data
    When an instance is started
    Then no tag on any measurement contains that secret
