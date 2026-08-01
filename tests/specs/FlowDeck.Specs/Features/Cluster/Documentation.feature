@M6
Feature: Multi-node operation is documented
  The limits of running more than one node are written where an operator and an
  author will look, not only in a decision record.

  @issue-150
  Scenario: The guide has a multi-node section
    Given the multi-node section of the usage guide
    Then it states that nodes are symmetric with no leader
    And it states that recovery is not load balancing
    And it states that a lapsed lease can cause a duplicate step execution
    And it states that nodes assume roughly agreed clocks

  @issue-150
  Scenario: The lifecycle table and limitations stay current
    Given the workflow guide
    Then the known limitations no longer claim a crash strands an instance
    And they record what multi-node execution still does not do
