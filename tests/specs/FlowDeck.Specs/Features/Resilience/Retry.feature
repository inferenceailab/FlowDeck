@M5
Feature: Retry
  A step may be attempted more than once. Retry is opt-in, bounded, and backs
  off between attempts so a struggling service is not hammered.

  @issue-103
  Scenario: A step with no policy does not retry
    Given a step declared without a retry policy
    And the step always throws
    When a retrying instance is started
    Then the step executes exactly 1 time
    And the retrying instance status becomes Failed

  @issue-103
  Scenario: A step retries up to its attempt limit
    Given a step declared with a retry policy allowing 3 attempts
    And the step always throws
    When a retrying instance is started
    Then the step executes exactly 3 times
    And the retrying instance status becomes Failed

  @issue-103
  Scenario: A step that succeeds on retry completes the workflow
    Given a step declared with a retry policy allowing 3 attempts
    And the step throws once and then succeeds
    When a retrying instance is started
    Then the retrying instance status becomes Completed

  @issue-104
  Scenario: Steps inherit the workflow default
    Given a workflow declaring a default policy of 2 attempts
    And a step declared without its own policy that always throws
    When a retrying instance is started
    Then the step executes exactly 2 times

  @issue-104
  Scenario: A step policy overrides the workflow default
    Given a workflow declaring a default policy of 2 attempts
    And a step declaring a policy of 4 attempts that always throws
    When a retrying instance is started
    Then the step executes exactly 4 times

  @issue-104
  Scenario: A step can opt out of the workflow default
    Given a workflow declaring a default policy of 3 attempts
    And a step declaring RetryPolicy.None that always throws
    When a retrying instance is started
    Then the step executes exactly 1 time

  @issue-105
  Scenario: Exponential backoff grows the delay
    Given an exponential policy with a base delay of 1 second
    When delays are computed for attempts 1, 2 and 3
    Then each delay is at least double the previous

  @issue-105
  Scenario: Jitter desynchronises instances
    Given an exponential policy with jitter
    When the delay for the same attempt is computed many times
    Then the values are not all identical
    And every value is within the policy bounds

  @issue-105
  Scenario: A fixed policy waits the same each time
    Given a fixed policy of 2 seconds
    When delays are computed for attempts 2, 3 and 4
    Then every delay is 2 seconds

  @issue-105
  Scenario: The engine waits between attempts
    Given a step with a policy allowing 3 attempts and a 2 second delay
    And the step throws twice and then succeeds
    When a retrying instance runs on a controlled clock
    Then the gap between attempts is 2 seconds
    And no real time passes

  @issue-106
  Scenario: The attempt count survives a restart
    Given a step with a policy allowing 3 attempts
    And the step has already failed twice
    When the host restarts and the instance resumes
    Then the step executes exactly 1 time
    And the retrying instance status becomes Failed

  @issue-106
  Scenario: The attempt count resets when execution advances
    Given a step that failed once and then succeeded
    And a later step that always throws with 2 attempts allowed
    When a retrying instance is started
    Then the later step is attempted 2 times

  @issue-107
  Scenario: Each attempt appends a history entry
    Given a step with a policy allowing 3 attempts
    And the step always throws
    When a retrying instance is started
    Then the history contains three entries for that step
    And each records its own error

  @issue-107
  Scenario: The attempt number is visible
    Given a step with a policy allowing 3 attempts
    And the step throws twice and then succeeds
    When a retrying instance is started
    Then the history reports attempts 1, 2 and 3
