namespace WakeMesh.Api.Tests;

/// <summary>
/// Opt-in real PostgreSQL gate. Skips unless WAKEMESH_TEST_POSTGRES is set
/// (scripts/pg-test-local.sh env exports a disposable local instance).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAKEMESH_TEST_POSTGRES")))
        {
            Skip = "Set WAKEMESH_TEST_POSTGRES (via scripts/pg-test-local.sh env) to run real PostgreSQL tests.";
        }
    }
}

/// <summary>Same opt-in gate for data-driven cases.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAKEMESH_TEST_POSTGRES")))
        {
            Skip = "Set WAKEMESH_TEST_POSTGRES (via scripts/pg-test-local.sh env) to run real PostgreSQL tests.";
        }
    }
}
