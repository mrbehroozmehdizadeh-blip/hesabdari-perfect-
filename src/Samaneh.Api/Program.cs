var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "سامانه مالی و جامع هوشمند",
    service = "Samaneh.Api",
    status = "running",
    maturity = "prototype"
}));

app.MapHealthChecks("/health");

app.Run();

public partial class Program;
