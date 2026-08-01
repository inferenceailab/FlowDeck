@M7
Feature: Run overlay
  A run drawn on the shape it is running. The timeline says what happened in
  order; the shape says where in the workflow it happened, which is the question
  a list of step names makes an operator reconstruct for themselves.

  @issue-181
  Scenario: Steps that ran are marked on the shape
    Given an instance whose first two steps succeeded
    When I open its detail view
    Then those two steps are marked as run on the shape
    And the step that has not run is not marked as run

  @issue-181
  Scenario: The failed step is marked where it happened
    Given an instance that failed at a step inside a branch
    When I open its detail view
    Then that step is marked as failed on the shape
    And the branch containing it is the one shown as taken

  @issue-181
  Scenario: A branch a choice did not take is marked as not taken
    Given an instance whose choice took "in-stock"
    When I open its detail view
    Then the steps under "backorder" are marked as not taken
    And they are distinguishable from steps that have simply not run yet

  @issue-181
  Scenario: A fork marks every branch, because every branch runs
    Given a forked instance where one branch finished and the other did not
    When I open its detail view
    Then no branch of the fork is marked as not taken

  @issue-181
  Scenario: A rolled-back step is distinguishable from a completed one
    Given a compensated instance
    When I open its detail view
    Then the rolled-back steps are marked as undone rather than as run

  @issue-181
  Scenario: The shape not loading does not cost the timeline
    Given an instance whose definition version is no longer registered
    When I open its detail view
    Then the timeline and the failure are still shown
    And the shape is reported as unavailable rather than blanking the view
