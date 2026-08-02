@M10
Feature: The operator actions are documented
  Every one of these is reached at 2am. An operator should learn which are
  irreversible from the guide rather than from having used one.

  @issue-220
  Scenario: The guide lists every action and says which cannot be undone
    Given the operations guide
    Then it names resume, suspend, retry, cancel and cancel-and-roll-back
    And it says which are irreversible

  @issue-220
  Scenario: The guide states that retry changes the instance id
    Given the operations guide
    Then it states that retry starts a new instance rather than reopening the old one
    And it states that the instance id changes
    And it says which retry to use when

  @issue-220
  Scenario: The guide explains when suspend takes effect
    Given the operations guide
    Then it states that suspend does not stop the running step
    And it says why the engine cannot interrupt one
    And it warns that the instance stays Running until that step finishes

  @issue-220
  Scenario: The guide states what bulk actions guarantee
    Given the operations guide
    Then it states that bulk actions are not atomic
    And it states that the per-item report must be read
    And it states the cap and what truncation means

  @issue-220
  Scenario: The guide states what is deliberately absent
    Given the operations guide
    Then it states that workflow data cannot be edited
    And it states that FlowDeck does not record who performed an action
