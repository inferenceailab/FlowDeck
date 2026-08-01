@M7
Feature: Recovering a forked instance
  NFR-1 under branching. Recovery that re-ran a completed step on a sibling
  branch would be worse than no recovery, and the single index that made this
  simple no longer describes where a forked instance is.

  @issue-166
  Scenario: Recovery resumes every branch that had not finished
    Given a forked instance whose first branch completed before the crash
    When another node recovers it
    Then only the unfinished branch resumes

  @issue-166
  Scenario: A completed step on any branch is never re-executed
    Given a forked instance with completed steps on both branches
    When another node recovers it
    Then no completed step runs again

  @issue-166
  Scenario: The step that opened the fork is not re-executed
    Given a forked instance whose first branch completed before the crash
    When another node recovers it
    Then the step that opened the fork does not run again

  @issue-166
  Scenario: Recovery never records a position on a finished branch
    Given a forked instance whose first branch completed before the crash
    When another node recovers it
    Then no checkpoint it writes names a step of the finished branch

  @issue-166
  Scenario: A recovered choice stays on the branch it took
    Given a conditional instance that crashed inside the branch it took
    And the data that chose it has since changed
    When another node recovers it
    Then it resumes on the branch it had taken

  @issue-166
  Scenario: A recovered fork still joins and completes
    Given a forked instance with completed steps on both branches
    When another node recovers it
    Then the step after the join runs once and the instance completes
