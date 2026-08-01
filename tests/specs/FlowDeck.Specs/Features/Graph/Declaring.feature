@M7
Feature: Declaring branches and forks
  A workflow is no longer a straight line. A step can offer named branches of
  which one is taken, a branch can carry a condition over workflow data, and a
  fork runs every branch.

  @issue-162
  Scenario: A step declares named branches
    Given a workflow whose step declares branches "in-stock" and "backorder"
    When the definition is compiled
    Then both branches are part of the graph
    And neither carries a condition

  @issue-162
  Scenario: A predicate branch is declared with its condition
    Given a workflow declaring a branch when the order total exceeds 1000
    When the definition is compiled
    Then that branch carries a condition the graph can report
    And the condition selects the branch for a total of 1500
    And it does not select the branch for a total of 500

  @issue-162
  Scenario: A parallel fork declares independent branches
    Given a workflow forking into two branches that rejoin
    When the definition is compiled
    Then both branches are part of the graph
    And both are marked parallel
    And they converge on the step declared after the fork

  @issue-162
  Scenario: Branches attach to the step just declared
    Given a workflow declaring two steps, the first with branches
    When the definition is compiled
    Then only the first step carries branches

  @issue-162
  Scenario: A branch declared before any step is rejected
    Given a workflow calling Branch before AddStep
    When a graph instance is started
    Then InvalidWorkflowDefinitionException is raised

  @issue-162
  Scenario: Two branches with the same name are rejected
    Given a step declaring two branches both named "retry"
    When a graph instance is started
    Then InvalidWorkflowDefinitionException is raised

  @issue-162
  Scenario: A branch declaring no steps is rejected
    Given a step declaring an empty branch
    When a graph instance is started
    Then InvalidWorkflowDefinitionException is raised

  @issue-162
  Scenario: A step name must be unique across the whole graph
    Given a workflow reusing the step name "charge" inside a branch
    When a graph instance is started
    Then InvalidWorkflowDefinitionException is raised
