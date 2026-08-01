@M9
Feature: Filtering instances by definition version
  "Is anything still running v1" is the question an operator has to answer
  before removing a version, and the store could not be asked it.

  @issue-202
  Scenario: Counting instances of one version ignores the others
    Given instances of "orders" v1 and "orders" v2
    When I count instances of "orders" v1
    Then only the v1 instances are counted

  @issue-202
  Scenario: Only instances that can still execute are counted
    Given a completed, a cancelled and a suspended instance of "orders" v1
    When I count the instances still holding "orders" v1
    Then only the suspended one is counted

  @issue-202
  Scenario Outline: Every provider filters by version identically
    Given a <provider> store holding instances of two versions
    When they are listed with a version filter
    Then only that version's instances come back

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |
