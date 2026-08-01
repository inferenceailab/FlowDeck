@M2
Feature: Retention
  Completed instances are swept after a retention period, so the store does not
  grow without bound. Work that has not finished is never swept, however old.

  @issue-20
  Scenario: Instances older than the retention window are purged
    Given retention is configured to 30 days
    And a completed instance finished 31 days ago
    When the purge job runs
    Then that instance is removed

  @issue-20
  Scenario: In-flight instances are never purged
    Given a suspended instance created 90 days ago
    When the purge job runs
    Then that instance is retained
