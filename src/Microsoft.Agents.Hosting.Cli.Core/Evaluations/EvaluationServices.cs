namespace Microsoft.Agents.Hosting.Cli.Core.Evaluations;

/// <summary>The evaluation command services, grouped so the operation facade takes one dependency.</summary>
/// <param name="Queries">Lists and reads runs and test sets.</param>
/// <param name="Runs">Starts, polls, stops and deletes runs, and generates conversations.</param>
/// <param name="TestSets">Creates, updates and deletes test sets.</param>
public sealed record EvaluationServices(
    EvaluationQueryService Queries,
    EvaluationRunService Runs,
    EvaluationTestSetService TestSets);
