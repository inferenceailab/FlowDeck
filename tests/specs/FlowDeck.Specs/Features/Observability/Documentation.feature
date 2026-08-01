@M8
Feature: What FlowDeck emits is documented and bounded
  An operator has to wire this up without reading the engine, and has to be able
  to rely on the one promise that matters: that nothing an author put in
  workflow data leaves the process.

  @issue-190
  Scenario: The guide lists every signal
    Given the observability guide
    Then it names every metric with its type and labels
    And it names both spans and the attributes they carry
    And it names every log event with its level
    And it shows how to scrape metrics and how to enable OTLP

  @issue-190
  Scenario: The guide states the data boundary and why it is drawn there
    Given the observability guide
    Then it states that no workflow data reaches a log, span or metric
    And it says a span is a leakier place than the store, and why
    And it says that keys were rejected along with values

  @issue-190
  Scenario: The guide names what is not measured
    Given the observability guide
    Then it names step duration and cluster health as deliberately absent

  @issue-190
  Scenario: No workflow data escapes into any signal
    Given a workflow whose steps read and write a secret through every path
    When it runs to completion with logging, metrics and tracing all captured
    Then the secret appears in no log entry
    And it appears in no span
    And it appears in no measurement

  @issue-190
  Scenario: No workflow data escapes when a run fails and rolls back
    Given a workflow that puts a secret in workflow data and then fails
    When it runs with logging, metrics and tracing all captured
    Then the secret appears in nothing the engine emitted
    And the failure was still reported
