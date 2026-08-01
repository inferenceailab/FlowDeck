@M6
Feature: Claiming an instance
  A node claims an instance before running it, and renews while it works. Two
  nodes must never hold the same instance at once.

  @issue-144
  Scenario: Claiming an unowned instance succeeds
    Given a suspended instance with no owner
    When node A claims it
    Then node A owns it
    And the lease expires in the future

  @issue-144
  Scenario: A second node cannot claim an owned instance
    Given an instance owned by node A with a live lease
    When node B tries to claim it
    Then the claim is refused
    And node A still owns it

  @issue-144
  Scenario: Two nodes claiming at the same moment
    Given a suspended instance with no owner
    When node A and node B both read it before either writes
    And both then try to claim it
    Then exactly one claim succeeds

  @issue-144
  Scenario: An expired lease can be claimed by another node
    Given an instance owned by node A whose lease has expired
    When node B tries to claim it
    Then node B owns it

  @issue-144
  Scenario: A terminal instance is never claimable
    Given a completed instance
    When node A tries to claim it
    Then the claim is refused

  @issue-145
  Scenario: Renewal extends the lease
    Given an instance owned by node A
    When node A renews the lease
    Then the expiry moves further into the future

  @issue-145
  Scenario: A node cannot renew a lease it does not hold
    Given an instance owned by node A
    When node B tries to renew it
    Then the renewal is refused
    And node A still owns it

  @issue-145
  Scenario: A node that lost its lease stops at the next checkpoint
    Given node A is running an instance
    And node B has taken the lease
    When node A reaches its next checkpoint
    Then the save is rejected and node A stops
