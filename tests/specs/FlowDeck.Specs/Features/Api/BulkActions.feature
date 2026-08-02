@M10
Feature: Bulk operator actions
  Clearing up after an incident should not be fifty clicks. Fifty instances are
  fifty independent operations, so this is best-effort with a per-item report —
  a bulk action that half-worked and said nothing is worse than none.

  @issue-219
  Scenario: Cancelling a filtered set cancels each one
    Given five suspended instances of "orders"
    When I bulk cancel instances of "orders"
    Then all five are Cancelled
    And the report says five succeeded and none failed

  @issue-219
  Scenario: One refusal does not stop the rest
    Given four suspended instances and one already completed
    When I bulk cancel instances of "orders"
    Then the four suspended ones are Cancelled
    And the report names the one that was refused, and why

  @issue-219
  Scenario: Bulk retry starts one new instance per failed one
    Given three failed instances of "orders"
    When I bulk retry instances of "orders"
    Then three new instances were started
    And each result links its new instance to the original

  @issue-219
  Scenario: The filter decides what is touched
    Given suspended instances of "orders" and of "refunds"
    When I bulk cancel instances of "orders"
    Then only the "orders" instances are Cancelled

  @issue-219
  Scenario: A set larger than the cap is truncated and says so
    Given more suspended instances than the page cap allows
    When I bulk cancel instances of "orders"
    Then no more than the cap were attempted
    And the report says the set was truncated
