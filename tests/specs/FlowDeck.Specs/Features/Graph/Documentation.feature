@M7
Feature: Branching and parallel execution are documented
  Concurrency makes a new class of problem the author's, not the engine's. The
  guide has to say so where an author will look, before production says it for
  them.

  @issue-167
  Scenario: The guide has a branching and parallel section
    Given the branching section of the usage guide
    Then it shows how to declare a named branch and a predicate branch
    And it states that parallel branches run genuinely concurrently
    And it states that workflow data is shared and only individually thread-safe
    And it states that a join waits for every branch and any failure fails the instance

  @issue-167
  Scenario: The section says what the engine does not do
    Given the branching section of the usage guide
    Then it states that a choice with no matching condition takes no branch
    And it states that suspending inside a branch is not supported
    And it states that compensation is ordered by completion, not by declaration

  @issue-167
  Scenario: The limitations table names the branch suspension limit
    Given the workflow guide
    Then the known limitations table names suspending inside a branch

  @issue-167
  Scenario: The rules table names what a branch declaration rejects
    Given the workflow guide
    Then the rules table names the branch declarations the engine rejects
