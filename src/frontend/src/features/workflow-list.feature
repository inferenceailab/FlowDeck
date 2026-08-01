@M9
Feature: Workflow list
  What is registered on this host, and what is still running each version.
  Retirement is refused while instances hold a version, so an operator who
  cannot see that has to attempt a removal to find out.

  @issue-205
  Scenario: Every registered version is listed
    Given two versions of "orders" are registered
    When I open the workflows view
    Then both versions are shown with their ids and versions

  @issue-205
  Scenario: A version something is running says it cannot be retired
    Given "orders" v1 has one live instance and v2 has none
    When I open the workflows view
    Then v1 says it cannot be retired, and how many are running
    And v2 says it is safe to retire

  @issue-205
  Scenario: A count of zero is not read as busy
    Given a version reporting zero live instances as a string
    When I open the workflows view
    Then it is shown as safe to retire

  @issue-205
  Scenario: A host with nothing registered says so
    Given no workflows are registered
    When I open the workflows view
    Then it says nothing is registered rather than showing an empty table
