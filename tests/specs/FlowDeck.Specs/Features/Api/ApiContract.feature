@M3
Feature: The API contract
  Errors are RFC 9457 problem details, the surface is described by an OpenAPI
  document, and the process reports whether it can serve traffic.

  @issue-27
  Scenario: Validation failure returns problem details
    When I POST an instance start request with an invalid body
    Then the response status is 400
    And the content type is application/problem+json
    And the body contains type, title, status and detail

  @issue-28
  Scenario: OpenAPI document is served
    When I GET /openapi/v1.json
    Then the response status is 200
    And the document lists every public endpoint

  @issue-29
  Scenario: Healthy service reports ready
    Given the persistence store is reachable
    When I GET /health/ready
    Then the response status is 200

  @issue-29
  Scenario: Unreachable store reports not ready
    Given the persistence store is unreachable
    When I GET /health/ready
    Then the response status is 503
