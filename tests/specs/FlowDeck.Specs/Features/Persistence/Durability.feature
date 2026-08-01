@M2
Feature: Durability and recovery
  Instance state is checkpointed after every step, so a restart loses at most one
  step of progress and never re-runs work that already completed.

  @issue-13
  Scenario: State is written after each step
    Given a three step workflow
    When the instance executes to completion
    Then the persistence provider received at least three saves
    And the final saved state has status Completed

  @issue-14
  Scenario: Suspended instance resumes on a new host
    Given an instance suspended after step A
    And the engine host is restarted
    When the engine resumes pending instances
    Then step B executes
    And step A is not executed a second time

  @issue-15
  Scenario: Context survives a restart
    Given step A wrote "orderId" = 42 before suspension
    When the instance resumes after a restart
    Then step B reads "orderId" as 42

  @issue-22
  Scenario: Crash during step C keeps A and B results
    Given steps A and B have completed
    And step C crashes the host process
    When the engine restarts and resumes the instance
    Then steps A and B are not re-executed
    And execution resumes at step C
