// Startup stub for `dotnet ef` only. This assembly contains PostgreSQL migrations
// for WakeMeshDbContext; it never runs as the production API host.
//
// The API project deliberately does NOT reference this assembly, so runtime API
// startup cannot apply PostgreSQL migrations (migrations are an explicit deploy step).
using WakeMesh.Api.Data;

namespace WakeMesh.Api.PostgresMigrations;

internal static class Program
{
    private static void Main(string[] args)
    {
        _ = typeof(WakeMeshDbContext).Assembly.GetName().Name;
        Console.WriteLine(
            "WakeMesh.Api.PostgresMigrations contains PostgreSQL EF migrations only. " +
            "Run 'dotnet ef migrations ...' with WAKEMESH_DESIGN_PROVIDER=Postgres.");
    }
}
