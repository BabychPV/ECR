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
// ⚠ Конкретний CurrentUser реєструється ОКРЕМО, і той самий екземпляр
// віддається за інтерфейсом. Контролери, яким архітектурне правило
// дозволяє брати CurrentUser напряму (CellsController,
// TemplateVersionsController), без цієї реєстрації просто не
// створювалися б — і 500 отримували б усі їхні ендпоінти.
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<Ecr.Application.Common.ICurrentUser>(
    sp => sp.GetRequiredService<CurrentUser>());
// ⚠ Метрики бюджету — фільтром, а не викликом у кожній дії: метрика, яку
// треба не забути дописати, рано чи пізно не дописується. До цього
// `EcrMetrics` існував і не викликався жодного разу (аудит Етапу 5).
builder.Services.AddSingleton<Ecr.Api.Observability.EcrMetrics>();
// ⚠ ConsistencyCheckJob живе в Ecr.Infrastructure, яка Ecr.Api не бачить:
// адаптер закриває EcrMetrics портом IConsistencyMetrics, щоб задача могла
// викликати метрику, не порушуючи напрямок залежностей (директива №11, T10
// #41 — RecordConsistencyIssues існував і не мав жодного викликача).
builder.Services.AddSingleton<Ecr.Application.Ports.IConsistencyMetrics,
    Ecr.Api.Observability.ConsistencyMetricsAdapter>();
builder.Services.AddScoped<Ecr.Api.Observability.BudgetMetricsFilter>();
builder.Services
    .AddControllers(options => options.Filters.Add<Ecr.Api.Observability.BudgetMetricsFilter>())
    .AddJsonOptions(options =>
    {
        // ⚠ Переліки йдуть ІМЕНАМИ, а не числами. За замовчуванням
        // System.Text.Json пише `1`, і клієнт отримує статус версії,
        // стан періоду й рівень методології як безіменні числа: показати їх
        // користувачеві не можна, порівняти зі значенням — теж (`A7-07`).
        //
        // ⛔ Числа ще й НЕСТАБІЛЬНІ як контракт: вставка нового члена в
        // середину переліку мовчки змінює значення всіх наступних, і клієнт
        // починає показувати «Approved» там, де сервер має на увазі
        // «Submitted». Ім'я такого не вміє.
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// ⚠ Ті самі налаштування — і для генератора OpenAPI. Він читає JSON-опції
// мінімальних API (`Microsoft.AspNetCore.Http.Json`), а не MVC: без цього
// рядка сервер віддавав би імена, а схема описувала б числа — і згенерований
// клієнт розходився б із дійсністю в протилежний бік.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
// ⚠ Постійні розклади ставить hosted service, а не крок старту: планувальник
// Quartz стає придатним лише після ApplicationStarted. Без цієї реєстрації
// вночі мовчазно не відбувалася б жодна перевірка.
builder.Services.AddHostedService<Ecr.Api.Startup.RecurringScheduleService>();
// ⚠ Трансформер словників обов'язковий: без нього `RowDto.cells` описано як
// об'єкт без дозволених властивостей, і згенерований клієнт не може покласти
// в комірку жодного значення (див. DictionarySchemaTransformer).
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<Ecr.Api.Startup.DictionarySchemaTransformer>();
    options.AddSchemaTransformer<Ecr.Api.Startup.NumericSchemaTransformer>();

    // ⛔ `/health/*` — middleware, а не контролер: генератор його не бачить,
    // і саме тому клієнт описував відповідь руками — звідси `A7-04`/`A7-36`.
    options.AddDocumentTransformer<Ecr.Api.Startup.HealthDocumentTransformer>();
});

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
app.UseMiddleware<PasswordChangeMiddleware>();   // разовий пароль закриває все, крім його зміни
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
