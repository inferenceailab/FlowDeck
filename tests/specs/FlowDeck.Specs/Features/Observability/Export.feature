@M8
Feature: Serving metrics and exporting traces
  The observability an operator gets should not depend on infrastructure they
  have not built. Metrics are scraped from an endpoint that is always there;
  traces go somewhere only when there is somewhere to send them.

  @issue-189
  Scenario: Metrics are served in Prometheus text format
    Given a host that has run two instances
    When I GET /metrics
    Then the response is Prometheus text format
    And it reports two started and two completed

  @issue-189
  Scenario: A counter's tags become labels
    Given a host that has run instances of two different definitions
    When I GET /metrics
    Then each definition appears as its own labelled series

  @issue-189
  Scenario: The endpoint names the counters before anything has run
    Given a host that has run nothing
    When I GET /metrics
    Then the response succeeds and declares every counter with its type

  @issue-189
  Scenario: A definition id containing a quote does not corrupt the response
    Given a host that has run an instance of a definition whose id contains a quote
    When I GET /metrics
    Then the quote is escaped rather than ending the label early

  @issue-189
  Scenario: Tracing is not wired when no endpoint is configured
    Given a host with no OTLP endpoint configured
    When an instance is started over HTTP
    Then it succeeds and nothing is exported

  @issue-189
  Scenario: Tracing exports when an endpoint is configured
    Given a collector listening on a local endpoint
    When an instance is started over HTTP on a host configured to export to it
    Then the collector receives the traces
