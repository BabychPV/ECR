using Ecr.Api.Auth;
using Ecr.Api.Errors;
using Ecr.Api.Middleware;
using Ecr.Api.Startup;
using Ecr.Application;
using Ecr.Infrastructure;
using Ecr.Calculations;
using Ecr.Adapters.Excel;
using Ecr.Adapters.PiAf;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Конфігурація: змінні оточення з префіксом ECR_ перекривають файли
builder.Configuration.AddEnvironmentVariables(prefix: "ECR_");

builder.Services.AddEcrInfrastructure(builder.Configuration);
builder.Services.AddEcrCalculations();
builder.Services.AddExcelAdapters();
builder.Services.AddPiAfAdapters();
builder.Services.AddEcrApplication();

builder.Services.AddEcrAuthentication(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<Ecr.Api.Health.DatabaseHealthCheck>("db")
    .AddCheck<Ecr.Api.Health.JobsHealthCheck>("jobs")
    .AddCheck<Ecr.Api.Health.SourcesHealthCheck>("sources");

var app = builder.Build();

// ⚠ ПОСЛІДОВНІСТЬ СТАРТУ (B01 §6.3) — порядок значущий:
// 1) retry-очікування БД  2) звірка міграцій  3) Validate/Migrate
// 4) ідемпотентний seed   5) валідація метаданих
// 6) прогрів кешу         7) перевірка запасу партицій
await app.RunEcrStartupSequenceAsync();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthentication();
app.UseMiddleware<SecurityStampMiddleware>();   // після автентифікації, до авторизації
app.UseAuthorization();

app.MapControllers();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.Run();

/// <summary>Точка входу; <c>public partial</c> — щоб тести могли підняти застосунок.</summary>
public partial class Program;
