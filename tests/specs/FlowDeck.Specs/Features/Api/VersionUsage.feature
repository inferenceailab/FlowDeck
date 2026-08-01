@M9
Feature: Reporting which definition versions are in use
  Retirement is refused while instances hold a version, so an operator needs to
  see what is holding one before they try — otherwise the only way to find out
  is to attempt the removal and read the error.

  @issue-205
  Scenario: The definitions list reports live instances per version
    Given "orders" v1 with a suspended instance and v2 with none
    When I GET the workflow definitions
    Then v1 reports one live instance
    And v2 reports none

  @issue-205
  Scenario: Terminal instances are not counted as live
    Given "orders" v1 whose only instance completed
    When I GET the workflow definitions
    Then v1 reports none

  @issue-205
  Scenario: The count agrees with what retirement allows
    Given "orders" v1 with a suspended instance and v2 with none
    When I GET the workflow definitions
    Then the version reporting none can be retired
    And the version reporting one cannot
