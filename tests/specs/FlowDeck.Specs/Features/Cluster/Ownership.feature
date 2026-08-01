@M6
Feature: Instance ownership
  An instance records which node is running it and until when. That is what
  tells a node that died apart from one that is simply busy.

  @issue-143
  Scenario: A new instance has no owner
    Given a freshly started instance
    Then it has no owner and no lease expiry

  @issue-143
  Scenario Outline: Ownership round-trips through the store
    Given the <provider> workflow store
    When an instance is saved with an owner and a lease expiry
    Then reading it back returns both

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |

  @issue-143
  Scenario Outline: A cleared lease round-trips as absent
    Given the <provider> workflow store
    When an instance with an owner has that owner cleared
    Then reading it back returns no owner

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |
