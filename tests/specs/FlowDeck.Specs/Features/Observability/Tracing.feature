@M8
Feature: Instance and step tracing
  A slow endpoint and the step responsible for it are the same incident. Without
  a trace spanning both they are two investigations, in two tools, joined by an
  operator's guess about which run was which.

  @issue-188
  Scenario: An instance run is one span
    Given a definition "order-fulfilment" version 3 with one step
    When an instance is started
    Then a workflow.instance span records the instance id, definition id and version

  @issue-188
  Scenario: Each step is a child span of its instance
    Given a definition with steps "reserve" and "charge"
    When an instance is started
    Then each step has a workflow.step span whose parent is the instance span

  @issue-188
  Scenario: A retried step opens a span per attempt
    Given a step that fails twice and then succeeds
    When an instance is started
    Then there are three step spans, numbered by attempt
    And the two that failed are marked as errors

  @issue-188
  Scenario: A failing step marks its span and the instance span
    Given a definition whose only step throws
    When an instance is started
    Then that step's span is marked an error carrying the exception type
    And the instance span is marked an error too

  @issue-188
  Scenario: An instance started inside a caller's trace continues it
    Given a definition "order-fulfilment" version 3 with one step
    When an instance is started inside a caller's span
    Then the instance span belongs to the caller's trace

  @issue-188
  Scenario: A resumed instance starts its own trace
    Given a suspended instance inside a caller's span
    When it is resumed inside an unrelated span
    Then the resumed instance span belongs to neither trace

  @issue-188
  Scenario: Branch steps are children of the instance, not of each other
    Given a definition that forks into steps "left" and "right"
    When an instance is started
    Then both branch step spans have the instance span as their parent
    And each carries the branch it ran on

  @issue-188
  Scenario: Spans carry no workflow data
    Given a definition whose step writes a secret into workflow data
    When an instance is started
    Then no attribute on any span contains that secret
