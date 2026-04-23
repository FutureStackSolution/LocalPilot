<task_type>unit_testing</task_type>

<instruction>
Generate high-coverage unit tests for the provided code block.
1. POSITIVE PATHS: Test standard expected inputs.
2. NEGATIVE PATHS: Test boundary conditions, nulls, and error states.
3. MOCKING: Identify any dependencies that should be mocked.
Use the testing framework detected in the project (e.g., xUnit, NUnit, MSTest).
</instruction>

<unit_under_test>
{codeBlock}
</unit_under_test>
