@M4
Feature: Application shell
  The dashboard has one frame: a header, primary navigation, and a routed
  outlet. Views own their own data; the shell owns none.

  @issue-31
  Scenario: Shell renders with primary navigation
    Given the application has loaded
    When I view any route
    Then the header and primary navigation are visible
    And the active route is highlighted
