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

  @issue-148
  Scenario: Ownership reaches the API
    Given an instance owned by "node-a" over HTTP
    When I read that instance over HTTP
    Then the body reports the owning node and lease expiry

  @issue-148
  Scenario: An unowned instance reports no node
    Given a completed instance with no owner over HTTP
    When I read that instance over HTTP
    Then the body reports no owning node

  @issue-148
  Scenario: An expired lease is called out by the API
    Given a Running instance whose lease has expired over HTTP
    When I read that instance over HTTP
    Then the body says it is awaiting recovery

  @issue-148
  Scenario: A healthy instance is not called out
    Given an instance owned by "node-a" over HTTP
    When I read that instance over HTTP
    Then the body does not say it is awaiting recovery

  # Without this, dropping the status check went unnoticed: a suspended
  # instance with a lapsed lease is the ordinary resting state of a workflow
  # waiting on something, and flagging every one of them would make the
  # notice meaningless.
  @issue-148
  Scenario: A suspended instance with a lapsed lease is not awaiting recovery
    Given a Suspended instance whose lease has expired over HTTP
    When I read that instance over HTTP
    Then the body does not say it is awaiting recovery

  @issue-143
  Scenario Outline: A cleared lease round-trips as absent
    Given the <provider> workflow store
    When an instance with an owner has that owner cleared
    Then reading it back returns no owner

    Examples:
      | provider  |
      | in-memory |
      | EF Core   |
