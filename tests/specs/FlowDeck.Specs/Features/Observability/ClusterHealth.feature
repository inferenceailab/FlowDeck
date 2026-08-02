@M8
Feature: Cluster health
  M6's machinery has no visible cost. A node quietly recovering work every few
  minutes means another node is dying repeatedly, and nothing surfaces that —
  the recovery itself is the system working, so there is no failure for anyone
  to notice.

  @issue-200
  Scenario: A node reports what it is running right now
    Given a host running an instance blocked inside a step
    When I GET /metrics
    Then it reports one instance executing

  @issue-200
  Scenario: The gauge falls back to zero once the run finishes
    Given a host that has finished an instance
    When I GET /metrics
    Then it reports no instances executing

  @issue-200
  Scenario: A run that failed still releases the gauge
    Given a host whose instance failed
    When I GET /metrics
    Then it reports no instances executing

  @issue-200
  Scenario: Recovered instances are counted
    Given two instances abandoned by a node that stopped
    When a dispatcher recovers them
    Then the recovery counter reports two, tagged with the node that did it

  @issue-200
  Scenario: Scraping twice does not accumulate
    Given a host running an instance blocked inside a step
    When I GET /metrics twice
    Then both scrapes report one instance executing

  @issue-200
  Scenario: The gauge is rendered as a gauge, not a counter
    Given a host that has finished an instance
    When I GET /metrics
    Then the executing metric is declared a gauge
    And it carries no _total suffix
