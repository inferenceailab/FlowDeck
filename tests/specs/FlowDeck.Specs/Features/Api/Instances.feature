@M3
Feature: The instances API
  Workflows are started, queried and cancelled over HTTP. Every scenario here
  drives the real pipeline, so routing, serialisation and status codes are all
  in scope.

  @issue-23
  Scenario: Starting a known definition returns 202
    Given a registered definition "order-fulfilment"
    When I POST /api/workflows/order-fulfilment/instances with a valid body
    Then the response status is 202
    And the body contains the new instance id
    And the Location header points at the instance resource

  @issue-23
  Scenario: Starting an unknown definition returns 404
    When I POST /api/workflows/does-not-exist/instances
    Then the response status is 404

  @issue-24
  Scenario: Known instance returns its state
    Given an existing instance
    When I GET the instance by id
    Then the response status is 200
    And the body contains status, current step and timestamps

  @issue-24
  Scenario: Unknown instance returns 404
    When I GET /api/instances/00000000-0000-0000-0000-000000000000
    Then the response status is 404

  @issue-25
  Scenario: Results are paged
    Given 150 existing instances
    When I GET /api/instances?page=1&pageSize=50
    Then exactly 50 instances are returned
    And the body reports a total count of 150

  @issue-25
  Scenario: Results can be filtered by status
    Given instances with mixed statuses
    When I GET /api/instances?status=Failed
    Then only failed instances are returned

  @issue-26
  Scenario: A running instance is cancelled
    Given a suspended instance exists
    When I POST the cancel endpoint for that instance
    Then the response status is 202
    And the instance status becomes Cancelled

  @issue-26
  Scenario: Cancelling a completed instance is a conflict
    Given a completed instance exists
    When I POST the cancel endpoint for that instance
    Then the response status is 409

  @issue-30
  Scenario: Registered definitions are listed
    Given two registered definitions
    When I GET /api/workflows
    Then both definitions are returned with their ids and versions
