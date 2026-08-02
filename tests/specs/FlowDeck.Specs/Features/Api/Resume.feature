@M10
Feature: Resuming an instance over HTTP
  ResumeAsync has existed since #12 and was reachable from nowhere: a suspended
  workflow could only be continued by code inside the process that started it.

  @issue-68
  Scenario: Resuming a suspended instance continues it
    Given a suspended instance
    When I POST to its resume endpoint
    Then the response status is 202
    And the instance has moved past the step it was parked on

  @issue-68
  Scenario: An instance that parks again reports Suspended
    Given a suspended instance whose step suspends every time
    When I POST to its resume endpoint
    Then the response reports it as Suspended

  @issue-68
  Scenario: Resuming a terminal instance is refused
    Given a completed instance
    When I POST to its resume endpoint
    Then the response status is 409

  @issue-68
  Scenario: Resuming an unknown instance is a 404
    When I POST to the resume endpoint of an unknown instance
    Then the response status is 404

  @issue-68
  Scenario: Two callers resuming the same instance do not both run it
    Given a suspended instance
    When two callers resume it one after the other
    Then the first succeeds and the second is refused

  @issue-68
  Scenario: An instance suspended by one host is resumable by another
    Given an instance suspended by a host that has since gone
    When a different host resumes it
    Then it continues from the step it was parked on
