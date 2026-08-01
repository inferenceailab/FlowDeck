@M9
Feature: The version lifecycle is documented
  An author who assumes redeploying fixes a stuck run will assume it exactly
  once, in production. The guide has to say otherwise where they will look.

  @issue-207
  Scenario: The guide explains what a new version does to in-flight work
    Given the versioning section of the usage guide
    Then it states that an in-flight instance runs to completion on its own version
    And it states that a bug in a deployed version cannot be fixed for instances already running it
    And it says what the remedies actually are

  @issue-207
  Scenario: The guide explains retirement
    Given the versioning section of the usage guide
    Then it states that retiring a version instances still hold is refused
    And it states that the refusal says how many
    And it says how to find out what is holding one

  @issue-207
  Scenario: The limitations table names what versioning does not do
    Given the workflow guide
    Then the known limitations table names the absence of migration
    And it names multi-tenancy as a decision rather than an omission
