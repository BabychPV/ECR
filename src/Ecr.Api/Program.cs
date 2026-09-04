using Ecr.Api.Auth;
using Ecr.Api.Errors;
using Ecr.Api.Middleware;
using Ecr.Api.Startup;
using Ecr.Application;
using Ecr.Infrastructure;
using Ecr.Calculations;
using Ecr.Adapters.Excel;
using Ecr.Adapters.PiAf;
using Ecr.Api.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Ecr.Application.Common.ICurrentUser, CurrentUser>();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// ⚠ Теги розділяють перевірки за призначенням: /health/live не має права
// торкатися БД — його опитує оркестратор, і повільна база не привід
// перезапускати процес, який працює.
builder.Services.AddHealthChecks()
    .AddCheck<Ecr.Api.Health.DatabaseHealthCheck>("db", tags: ["db", "ready"])
    .AddCheck<Ecr.Api.Health.JobsHealthCheck>("jobs", tags: ["ready"])
    .AddCheck<Ecr.Api.Health.SourcesHealthCheck>("sources", tags: ["ready"]);

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
// /health/live — лише «процес живий». Жодної перевірки: будь-яке звернення
// до БД тут перетворило б перезапуск процесу на наслідок проблем із базою.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponse.WriteAsync,
});

// /health/db віддає ПОДРОБИЦІ: режим редакції, RCSI, файлові групи, запас
// партицій і перелік того, що в цьому режимі недоступне (АРХ-7 п. 5).
app.MapHealthChecks("/health/db", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("db"),
    ResponseWriter = HealthResponse.WriteAsync,
});

app.Run();

/// <summary>Точка входу; <c>public partial</c> — щоб тести могли підняти застосунок.</summary>
public partial class Program;
