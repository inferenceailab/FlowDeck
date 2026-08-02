@M8
Feature: Step duration
  "This workflow is slow" is a complaint. "This step is slow" is something an
  operator can act on, and history records the durations per instance without
  ever aggregating them.

  @issue-198
  Scenario: Each step execution is recorded
    Given a definition with steps "reserve" and "charge"
    When an instance is started
    Then each step's duration is recorded under its own name

  @issue-198
  Scenario: A retried step records every attempt
    Given a step that fails twice and then succeeds
    When an instance is started
    Then three durations are recorded for it
    And the failed attempts are tagged separately from the successful one

  @issue-198
  Scenario: The scrape endpoint renders it as a histogram
    Given a host that has run an instance
    When I GET /metrics
    Then the duration is declared as a histogram measured in seconds
    And it reports cumulative buckets, a sum and a count

  @issue-198
  Scenario: Every bucket edge is rendered, ending at +Inf
    Given a host that has run an instance
    When I GET /metrics
    Then every configured bucket edge appears
    And the last one is +Inf, carrying the total count

  @issue-198
  Scenario: A step slower than every bucket still counts
    Given a host where a step took a minute
    When I GET /metrics
    Then no bucket edge holds it
    And the total count includes it

  @issue-198
  Scenario: The histogram carries no workflow data
    Given a definition whose step writes a secret
    When an instance is started
    Then no tag on any measurement contains that secret
