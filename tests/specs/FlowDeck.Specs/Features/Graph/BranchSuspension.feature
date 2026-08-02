@M10
Feature: Suspending inside a branch
  A branch that parks does not abandon its siblings, for the same reason a
  branch that fails does not: abandoning one would not stop its side effects,
  only stop FlowDeck recording them. Siblings finish, and the instance settles
  at the join.

  @issue-179
  Scenario: A branch that suspends parks the instance
    Given a fork whose first branch suspends and whose second completes
    When an instance is started
    Then the instance is Suspended
    And it is not Failed

  @issue-179
  Scenario: Sibling branches run to completion
    Given a fork whose first branch suspends and whose second completes
    When an instance is started
    Then the sibling branch's steps all ran

  @issue-179
  Scenario: Resuming re-enters only the branch that parked
    Given a suspended forked instance whose sibling finished
    When it is resumed
    Then the parked step runs again
    And the sibling's steps do not run again

  @issue-179
  Scenario: A branch that suspends does not stop a sibling from failing the instance
    Given a fork whose first branch suspends and whose second fails
    When an instance is started
    Then the instance is Failed rather than Suspended
