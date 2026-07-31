@M1
Feature: Defining workflows
  A workflow is a C# class with a stable identity. The engine executes the steps
  it declares, in the order it declares them.

  @issue-1
  Scenario: A definition exposes id and version
    Given a class implementing IWorkflowDefinition with id "order-fulfilment" and version 1
    When the definition is registered with the engine
    Then the registry returns it for id "order-fulfilment" version 1

  @issue-1
  Scenario: Duplicate id and version is rejected
    Given a definition "order-fulfilment" version 1 is already registered
    When a second definition with the same id and version is registered
    Then registration fails with a DuplicateDefinitionException

  @issue-9
  Scenario: Unknown definition id is rejected
    Given no definition registered with id "does-not-exist"
    When an instance of "does-not-exist" is started
    Then a DefinitionNotFoundException is thrown
    And no instance is created

  @issue-3
  Scenario: Single step workflow completes
    Given a registered definition containing exactly one step
    When an instance is started
    Then the step executes exactly once
    And the instance status becomes Completed

  @issue-4
  Scenario: Three steps run in declaration order
    Given a definition declaring steps A, B and C in that order
    When an instance is started
    Then the execution log records A then B then C

  @issue-4
  Scenario: A failing step halts the sequence
    Given a definition declaring steps A, B and C
    And step B throws an exception
    When an instance is started
    Then step C is never executed
    And the instance status becomes Failed
