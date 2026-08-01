@M9
Feature: Retiring a definition version
  A host that simply stops registering a version strands every in-flight
  instance of it: resume and recovery both resolve through the registry, so the
  instance becomes unresumable and nothing says so. Retirement makes that
  impossible rather than merely documented.

  @issue-203
  Scenario: Retiring an unused version succeeds
    Given a definition "orders" v1 with no live instances
    When I retire "orders" v1
    Then it is no longer registered
    And starting an instance of "orders" v1 is refused

  @issue-203
  Scenario: Retiring a version instances still hold is refused
    Given a suspended instance of "orders" v1
    When I retire "orders" v1
    Then the call fails, naming how many instances still hold it
    And "orders" v1 is still registered

  @issue-203
  Scenario: A terminal instance does not hold a version open
    Given a completed instance of "orders" v1
    When I retire "orders" v1
    Then it is no longer registered
    And that instance is still readable

  @issue-203
  Scenario: Retiring one version leaves the others alone
    Given "orders" v1 and v2 are registered
    When I retire "orders" v1
    Then "orders" v2 still starts instances

  @issue-203
  Scenario: Retiring a version that was never registered says so
    Given "orders" v1 is registered
    When I retire "orders" v9
    Then the call fails saying no such definition is registered
