@M1
Feature: Sharing data between steps
  Steps in one instance share a typed bag of state. Two instances of the same
  definition never see each other's.

  @issue-5
  Scenario: A later step reads an earlier step's output
    Given step A writes "orderId" = 42 to the workflow data
    When step B executes
    Then step B reads "orderId" as 42

  @issue-5
  Scenario: Context mutations are isolated per instance
    Given two concurrent instances of the same definition
    When instance 1 writes "orderId" = 1 and instance 2 writes "orderId" = 2
    Then each instance reads back only its own value

  @issue-10
  Scenario: Typed input is available to the first step
    Given a definition typed on input OrderRequest
    When an instance is started with OrderRequest with Id 7
    Then the first step reads Input.Id as 7

  @issue-10
  Scenario: Input type mismatch is rejected
    Given a definition typed on input OrderRequest
    When an instance is started with an input of a different type
    Then the start call fails with an InvalidInputTypeException
