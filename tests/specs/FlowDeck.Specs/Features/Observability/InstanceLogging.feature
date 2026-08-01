@M8
Feature: Instance lifecycle logging
  The engine has been silent since M1. History says what happened after the
  fact; an operator watching a run needs to be told while it is happening, and
  told which instance they are being told about.

  @issue-185
  Scenario: Starting an instance is logged with its identity
    Given a definition "order-fulfilment" version 3 with one step
    When an instance is started
    Then a log entry records that it started
    And that entry carries the instance id, definition id and version

  @issue-185
  Scenario: Each terminal outcome is logged distinctly
    Given a definition that completes, one that fails and one that is cancelled
    When each reaches its terminal state
    Then each logs its own outcome and not another's
    And the failure logs the failing step name and the error type

  @issue-185
  Scenario: Every entry emitted while an instance runs carries its id
    Given a definition "order-fulfilment" version 3 with one step
    When an instance is started
    Then every entry the engine emitted carries that instance id

  @issue-185
  Scenario: A failure is logged at a level that stands out from progress
    Given a definition whose only step throws
    When an instance is started
    Then the failure is logged as an error
    And no ordinary progress entry is logged as an error

  @issue-185
  Scenario: The engine runs without a logger
    Given a definition "order-fulfilment" version 3 with one step
    When an instance is started on an engine given no logger
    Then it completes
