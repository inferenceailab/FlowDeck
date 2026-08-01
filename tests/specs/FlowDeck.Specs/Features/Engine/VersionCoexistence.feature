@M9
Feature: Two definition versions execute side by side
  Shipping a change must not alter work already in flight. An instance runs the
  shape it started with until it settles, and there is no migration — so the
  engine has to be able to execute two versions of one workflow at once.

  @issue-204
  Scenario: An in-flight instance is unaffected by a newer version deploying
    Given a suspended instance of "orders" v1
    When "orders" v2 is registered with different steps
    And the v1 instance is resumed
    Then it executes v1's steps

  @issue-204
  Scenario: Both versions execute at the same time
    Given "orders" v1 and v2 declare different steps
    When an instance of each is started
    Then each executes its own version's steps

  @issue-204
  Scenario: Starting without a version takes the newest
    Given "orders" v1 and v2 declare different steps
    When an instance is started without naming a version
    Then it runs v2

  @issue-204
  Scenario: A recovered instance resumes on the version it started
    Given a crashed instance of "orders" v1 and a registered v2
    When another node recovers it
    Then it resumes v1's steps
