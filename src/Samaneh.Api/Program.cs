using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
}

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "سامانه مالی و جامع هوشمند",
    service = "Samaneh.Api",
    status = "running",
    maturity = "prototype"
}));

app.MapHealthChecks("/health");

app.MapGet("/health/db", async (IServiceProvider services, CancellationToken cancellationToken) =>
{
    var dataSource = services.GetService<NpgsqlDataSource>();
    if (dataSource is null)
        return Results.Problem(
            title: "Database is not configured",
            detail: "Set ConnectionStrings__Postgres to enable the PostgreSQL check.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Equals(result, 1)
            ? Results.Ok(new { database = "PostgreSQL", status = "connected" })
            : Results.Problem(title: "Unexpected database response", statusCode: 503);
    }
    catch (NpgsqlException)
    {
        return Results.Problem(
            title: "PostgreSQL connection failed",
            detail: "The API could not establish a database connection.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

public partial class Program { }
