# 05h — Скелет: `Ecr.Api`

> Частина [`05-skeleton.md`](05-skeleton.md).
> Ендпоінти — [`02-contracts.md#api-endpoints`](02-contracts.md#api-endpoints),
> помилки — [`#error-codes`](02-contracts.md#error-codes).

---

### `src/Ecr.Api/Program.cs`
MODULE: api | STAGE: 1
SCOPE: композиція застосунку і послідовність старту.
NOT IN SCOPE: бізнес-логіка в `Program.cs` — її тут не буває.

```csharp
using Ecr.Api.Auth;
using Ecr.Api.Errors;
using Ecr.Api.Middleware;
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
```

> `RunEcrStartupSequenceAsync` — розширення в
> `src/Ecr.Api/Startup/StartupSequence.cs`; повний вміст TODO — у
> [`05e`](05e-skeleton-infrastructure.md#7-старт-застосунку).

---

### `src/Ecr.Api/Auth/AuthenticationSetup.cs`
MODULE: api-auth | STAGE: 1
CONTRACT: 02-contracts.md#api-endpoints

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;

namespace Ecr.Api.Auth;

/// <summary>
/// Два провайдери автентифікації, **одна** cookie застосунку (ФВ-6.1).
/// </summary>
/// <remarks>
/// ⚠ Пастка розгортання, яка коштує дня на першому деплої (ТЗ §13.5 п.7):
/// в IIS має бути **увімкнено анонімний доступ** і **вимкнено Windows
/// Authentication на рівні сайту**. Negotiate застосовується **тільки** на
/// <c>/api/v1/login/windows</c> через атрибут. Інакше локальні користувачі
/// не дійдуть навіть до форми входу — браузер попросить доменні облікові дані
/// на будь-якому запиті.
/// </remarks>
public static class AuthenticationSetup
{
    /// <summary>Налаштовує схеми автентифікації.</summary>
    public static IServiceCollection AddEcrAuthentication(this IServiceCollection services, IConfiguration configuration)
        => throw new NotImplementedException(
            "TODO:\n" +
            "AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)\n" +
            "  .AddCookie(o => { o.Cookie.Name = Auth:CookieName; o.Cookie.HttpOnly = true;\n" +
            "      o.Cookie.SameSite = SameSiteMode.Strict;\n" +
            "      o.Cookie.SecurePolicy = Auth:RequireHttps ? Always : SameAsRequest;\n" +
            "      o.ExpireTimeSpan = TimeSpan.FromHours(Auth:SlidingHours);\n" +
            "      o.SlidingExpiration = true;\n" +
            "      o.Events.OnRedirectToLogin = 401 замість редиректу — це API, не MVC; })\n" +
            "  .AddNegotiate();\n" +
            "Обидва endpoint'и входу підписують ТУ САМУ cookie з тими самими claims — " +
            "нижче рівня входу авторизація не знає, як користувач увійшов (ФВ-6.2).");
}
```

---

### `src/Ecr.Api/Auth/SecurityStampMiddleware.cs`
MODULE: api-auth | STAGE: 3

```csharp
namespace Ecr.Api.Auth;

/// <summary>
/// Перевіряє <c>SecurityStamp</c> на кожен запит.
/// </summary>
/// <remarks>
/// Сенс саме в негайності: відкликана роль має перестати діяти **до**
/// завершення сесії, а не після закінчення cookie (тест безпеки №2).
/// </remarks>
public sealed class SecurityStampMiddleware(RequestDelegate next)
{
    /// <summary>Обробляє запит.</summary>
    public Task InvokeAsync(HttpContext context, Ecr.Infrastructure.Security.SecurityStampValidator validator)
        => throw new NotImplementedException(
            "TODO: якщо користувач автентифікований — узяти userId і stamp із claims, " +
            "звірити через validator; розбіжність → SignOut і 401 з ECR-AUTH-0401. " +
            "Анонімні запити пропускати без перевірки.");
}
```

---

### `src/Ecr.Api/Middleware/CorrelationIdMiddleware.cs`
MODULE: api | STAGE: 1

```csharp
namespace Ecr.Api.Middleware;

/// <summary>
/// Наскрізний ідентифікатор запиту. Потрапляє в логи, метрики, аудит і тіло
/// помилки — без нього звірити скаргу користувача з логом неможливо.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Заголовок кореляції.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Обробляє запит.</summary>
    public Task InvokeAsync(HttpContext context)
        => throw new NotImplementedException(
            "TODO: узяти X-Correlation-Id з запиту або згенерувати; покласти в HttpContext.Items " +
            "і в logging scope; повернути в заголовку відповіді.");
}
```

---

### `src/Ecr.Api/Errors/ExceptionHandlingMiddleware.cs`
MODULE: api-errors | STAGE: 1
CONTRACT: 02-contracts.md#error-model

```csharp
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;

namespace Ecr.Api.Errors;

/// <summary>
/// Перетворює винятки на <see cref="EcrProblemDetails"/>.
/// </summary>
/// <remarks>
/// Клієнт має розрізняти причини **за кодом**, а не парсити текст: саме тому
/// код стабільний, а повідомлення локалізоване і може змінюватися.
/// </remarks>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    /// <summary>Обробляє запит.</summary>
    public Task InvokeAsync(HttpContext context)
        => throw new NotImplementedException(
            "TODO: try/catch навколо next(context); мапінг:\n" +
            "  NotFoundException            → 404\n" +
            "  AccessDeniedException        → 403, у Extensions2.reason — EditDenyReason\n" +
            "  ConcurrencyConflictException → 409, у Extensions2.conflicts — перелік CellConflictDto\n" +
            "  BusinessRuleException        → 422, у Extensions2 — деталі правила\n" +
            "  DomainException              → 422 з ErrorCode\n" +
            "  решта                        → 500 ECR-SYS-0500\n" +
            "⚠ У відповідь на 500 НЕ включати текст винятку і стек: у логи — так, клієнту — " +
            "лише CorrelationId. Пароль і секрети не логуються ніколи (ФВ-6.11) — " +
            "це перевіряється окремим тестом.");
}
```

---

### `src/Ecr.Api/Errors/ErrorCodes.cs`
MODULE: api-errors | STAGE: 1
CONTRACT: 02-contracts.md#error-codes

```csharp
namespace Ecr.Api.Errors;

/// <summary>
/// Каталог кодів помилок. Код **стабільний**: клієнт і тести покладаються на
/// нього, тому текст змінювати можна, код — ні.
/// </summary>
public static class ErrorCodes
{
    // Автентифікація і доступ
    public const string Unauthorized = "ECR-AUTH-0401";
    public const string Forbidden = "ECR-AUTH-0403";
    public const string AccountLocked = "ECR-AUTH-0423";
    public const string AccessDenied = "ECR-ACCS-0403";

    // Шаблони і схема
    public const string TemplateNotFound = "ECR-TMPL-0404";
    public const string TemplateFrozen = "ECR-TMPL-0409";
    public const string TemplateInvalid = "ECR-TMPL-0422";
    public const string FormulaCycle = "ECR-TMPL-4221";
    public const string ReferenceUnresolved = "ECR-TMPL-4222";
    public const string UnitMismatch = "ECR-TMPL-4223";
    public const string BreakingChange = "ECR-SCHM-0409";
    public const string GuardedChangeWithoutStrategy = "ECR-SCHM-0422";

    // Документи, рядки, комірки
    public const string DocumentNotFound = "ECR-DOC-0404";
    public const string DocumentSubmitted = "ECR-DOC-0409";
    public const string DocumentCompositionInvalid = "ECR-DOC-0422";
    public const string RowNotFound = "ECR-ROW-0404";
    public const string RowDuplicate = "ECR-ROW-0409";
    public const string CellConflict = "ECR-CELL-0409";
    public const string CellInvalid = "ECR-CELL-0422";
    public const string CellComputed = "ECR-CELL-4221";
    public const string CellOutOfRange = "ECR-CELL-4222";

    // Періоди
    public const string PeriodClosed = "ECR-PRD-0409";
    public const string PeriodOutOfProject = "ECR-PRD-0422";
    public const string ReopenBlockedByPeriod = "ECR-PRD-4223";

    /// <summary><c>Sequence</c> поза діапазоном <c>1…12</c> (ФВ-1.5a, <c>D-108</c>).</summary>
    public const string PeriodSequenceOutOfRange = "ECR-PRD-4224";

    // Реєстри і одиниці
    public const string RegistryEntryNotFound = "ECR-REG-0404";
    public const string RegistryEntryInUse = "ECR-REG-0409";
    public const string RegistrySwitchInOpenPeriod = "ECR-REG-0422";
    public const string UnitDimensionMismatch = "ECR-UOM-0422";
    public const string UnitContextualCoefficient = "ECR-UOM-4221";

    // Розрахунки
    public const string MethodologyFourEyes = "ECR-CALC-0409";
    public const string MethodologyNoGreenTest = "ECR-CALC-0422";
    public const string RecalculateClosedPeriod = "ECR-CALC-4221";

    // Робочий процес
    /// <summary><c>Submit</c> при наявності рядків <c>IsOrphaned</c> (ФВ-8.13).</summary>
    public const string SubmitBlockedByOrphans = "ECR-SUB-4221";

    // Безпека: симуляція і зміна пароля
    /// <summary>Спроба запису в сеансі симуляції (<c>SimulationReadOnly</c>, ФВ-6.16a).</summary>
    public const string SimulationReadOnly = "ECR-SIM-0403";

    /// <summary>Симуляція самого себе або без причини.</summary>
    public const string SimulationInvalid = "ECR-SIM-0422";

    /// <summary>
    /// Потрібна зміна пароля: доки <c>MustChangePassword</c>, доступні лише
    /// зміна пароля і вихід (ФВ-6.18).
    /// </summary>
    public const string PasswordChangeRequired = "ECR-PWD-0428";

    // Імпорт та інтеграція
    public const string ImportStructureMismatch = "ECR-IMP-0422";
    public const string SourceUnavailable = "ECR-INT-0503";
    public const string SourceUnitChanged = "ECR-INT-0422";

    // Система
    public const string Internal = "ECR-SYS-0500";
    public const string Archiving = "ECR-SYS-0503";
}
```

---

### `src/Ecr.Api/Controllers/CellsController.cs`
MODULE: api-documents | STAGE: 1
CONTRACT: 02-contracts.md#dto
SCOPE: найгарячіший ендпоінт запису.
NOT IN SCOPE: бізнес-логіка — контролер лише транспорт.

```csharp
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Робота з комірками документа.</summary>
[ApiController]
[Route("api/v1/documents/{documentId:long}")]
[Authorize]
public sealed class CellsController(
    PatchCellsHandler patchHandler,
    GetTableSliceHandler sliceHandler,
    CreateRowHandler rowHandler,
    Ecr.Api.Auth.CurrentUser currentUser) : ControllerBase
{
    /// <summary>Зріз таблиці для grid.</summary>
    /// <remarks>Бюджет: p95 1.5 с на 500×60 (tz/08 §8.2).</remarks>
    [HttpGet("tables/{tableInstanceId:long}")]
    [ProducesResponseType<TableSliceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<TableSliceDto>> GetSlice(long documentId, long tableInstanceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: узяти AccessProfile із контексту (він уже в кеші сесії), викликати sliceHandler. " +
            "Жодної логіки в контролері.");

    /// <summary>Пакетна зміна комірок.</summary>
    /// <remarks>
    /// Часткове застосування заборонене: конфлікт у будь-якому рядку відхиляє
    /// весь батч із переліком розбіжностей (409 <c>ECR-CELL-0409</c>).
    /// </remarks>
    [HttpPatch("cells")]
    [ProducesResponseType<PatchCellsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<PatchCellsResponse>> Patch(
        long documentId, [FromBody] PatchCellsRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити, що request.TableInstanceId належить documentId; " +
            "викликати patchHandler. Винятки перетворює ExceptionHandlingMiddleware — " +
            "ловити їх тут не треба.");

    /// <summary>Додає рядок у динамічну таблицю.</summary>
    [HttpPost("rows")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public Task<IActionResult> CreateRow(long documentId, [FromBody] CreateRowRequest request, CancellationToken ct)
        => throw new NotImplementedException("TODO: делегувати rowHandler; повернути 201 із RowKey.");
}

/// <summary>Запит на створення рядка.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="RowKey">Бажаний ключ; <c>null</c> — згенерувати GUID.</param>
public sealed record CreateRowRequest(long TableInstanceId, string? RowKey);
```

---

### `src/Ecr.Api/Controllers/AuthController.cs`
MODULE: api-auth | STAGE: 1

```csharp
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Вхід і вихід. Два провайдери, одна cookie.</summary>
[ApiController]
[Route("api/v1")]
public sealed class AuthController : ControllerBase
{
    /// <summary>Вхід доменного користувача.</summary>
    /// <remarks>
    /// **Єдиний** endpoint, де застосовується Negotiate: на решті IIS має
    /// анонімний доступ, інакше локальні користувачі не увійдуть узагалі.
    /// </remarks>
    [HttpPost("login/windows")]
    [Authorize(AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme)]
    public Task<IActionResult> LoginWindows(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: узяти SID із WindowsIdentity; знайти або створити sec.User із Provider = Windows; " +
            "підписати ТУ САМУ cookie, що й локальний вхід, із claims: userId, userName, securityStamp; " +
            "записати aud.SecurityEvent і sec.LoginAttempt.");

    /// <summary>Вхід локального користувача.</summary>
    [HttpPost("login/local")]
    [AllowAnonymous]
    public Task<IActionResult> LoginLocal([FromBody] LocalLoginRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: знайти користувача з Provider = Local; перевірити LockedUntil; " +
            "звірити пароль через IPasswordHasher; при невдачі — інкремент FailedAttempts і, " +
            "за політикою, блокування (ECR-AUTH-0423); при успіху — скинути лічильник і " +
            "підписати cookie. " +
            "⚠ Повідомлення про помилку однакове для 'немає користувача' і 'невірний пароль' — " +
            "інакше endpoint стає засобом перебору імен.");

    /// <summary>Вихід.</summary>
    [HttpPost("logout")]
    [Authorize]
    public Task<IActionResult> Logout(CancellationToken ct)
        => throw new NotImplementedException("TODO: SignOutAsync і запис події.");

    /// <summary>Поточний користувач і його права для UI.</summary>
    [HttpGet("me")]
    [Authorize]
    public Task<IActionResult> Me(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: повернути userId, ім'я, мову і КОМПАКТНУ проєкцію AccessProfile — " +
            "функціональні права і гранти. Клієнт має знати їх наперед, щоб не показувати " +
            "кнопки, які все одно дадуть 403.");
}

/// <summary>Запит локального входу.</summary>
public sealed record LocalLoginRequest(string UserName, string Password);
```

---

### `src/Ecr.Api/Health/DatabaseHealthCheck.cs`
MODULE: api-health | STAGE: 1
CONTRACT: 02-contracts.md#health

```csharp
using Ecr.Application.Ports;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Стан БД: редакція, версія, RCSI, файлові групи, запас партицій.
/// </summary>
/// <remarks>
/// **Обов'язково повідомляє поточний режим редакції і що система в ньому
/// втрачає** — наприклад «перебудова індексів потребує вікна обслуговування»
/// (АРХ-7 п. 5). Health, який каже лише «healthy», не допомагає адміністратору
/// зрозуміти, чому нічна операція поводиться інакше, ніж на тесті.
/// </remarks>
public sealed class DatabaseHealthCheck(ISqlCapabilities capabilities, Ecr.Infrastructure.Persistence.EcrDbContext db)
    : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити з'єднання; зібрати data:\n" +
            "  edition, engineEdition, majorVersion, effectiveMode, rcsi,\n" +
            "  filegroups (наявність DATA_HOT/DATA_ARCHIVE/AUDIT/INDEXES),\n" +
            "  partitionsAhead (скільки вільних партицій попереду),\n" +
            "  limitations — перелік того, що недоступне в поточному режимі.\n" +
            "RCSI вимкнено → Unhealthy; partitionsAhead < 2 → Degraded.");
}
```

---

### `src/Ecr.Api/Observability/EcrMetrics.cs`
MODULE: api | STAGE: 1
CONTRACT: 02-contracts.md#observability

```csharp
using System.Diagnostics.Metrics;

namespace Ecr.Api.Observability;

/// <summary>
/// Метрики. Правило просте: якщо ендпоінт є в таблиці бюджету
/// (tz/08 §8.2) — він **зобов'язаний** мати метрику. Інакше твердження
/// «вкладаємося в бюджет» нічим не перевірити.
/// </summary>
public sealed class EcrMetrics
{
    /// <summary>Ім'я лічильника для OpenTelemetry.</summary>
    public const string MeterName = "Ecr";

    private readonly Histogram<double> _cellsRead;
    private readonly Histogram<double> _cellsWrite;
    private readonly Histogram<double> _formulaEvaluate;
    private readonly Histogram<double> _accessProfileBuild;
    private readonly Histogram<double> _jobDuration;
    private readonly Histogram<double> _calcFullYear;
    private readonly Counter<long> _conflicts;
    private readonly Counter<long> _consistencyIssues;

    /// <summary>Створює набір метрик.</summary>
    public EcrMetrics(IMeterFactory factory)
    {
        var meter = factory.Create(MeterName);
        _cellsRead = meter.CreateHistogram<double>("ecr.cells.read", "ms", "Відкриття зрізу таблиці");
        _cellsWrite = meter.CreateHistogram<double>("ecr.cells.write", "ms", "Пакетний запис комірок");
        _formulaEvaluate = meter.CreateHistogram<double>("ecr.formula.evaluate", "ms", "Перерахунок формул таблиці");
        _accessProfileBuild = meter.CreateHistogram<double>("ecr.access.profile.build", "ms", "Побудова AccessProfile");
        _jobDuration = meter.CreateHistogram<double>("ecr.job.duration", "s", "Тривалість фонової задачі");
        _calcFullYear = meter.CreateHistogram<double>("ecr.calc.full_year", "s", "Повний річний перерахунок");
        _conflicts = meter.CreateCounter<long>("ecr.conflict.count", "1", "Конфлікти паралельного редагування");
        _consistencyIssues = meter.CreateCounter<long>("ecr.consistency.issues", "1", "Знахідки ConsistencyCheckJob");
    }

    /// <summary>Фіксує тривалість читання зрізу.</summary>
    public void RecordCellsRead(double ms, int cellCount)
        => _cellsRead.Record(ms, new KeyValuePair<string, object?>("cells", cellCount));

    /// <summary>Фіксує тривалість запису.</summary>
    public void RecordCellsWrite(double ms, int cellCount)
        => _cellsWrite.Record(ms, new KeyValuePair<string, object?>("cells", cellCount));

    /// <summary>
    /// Фіксує повний річний перерахунок. **Бюджет — 600 с** (ПРД-13);
    /// перевищення має бути видно на графіку одразу.
    /// </summary>
    public void RecordFullYearCalculation(double seconds, int documentCount)
        => _calcFullYear.Record(seconds, new KeyValuePair<string, object?>("documents", documentCount));

    /// <summary>Фіксує конфлікт.</summary>
    public void RecordConflict() => _conflicts.Add(1);
}
```

---

## Решта контролерів

Створюються за тим самим зразком: `[ApiController]`, маршрут `api/v1/...`,
конструктор із хендлерами, атрибути `[ProducesResponseType]`, тіло —
`NotImplementedException` зі змістовним TODO. Логіки в контролері немає.

| Контролер | STAGE | Ендпоінти |
|---|---|---|
| `TemplatesController` | 1 | список, створення, версії |
| `TemplateVersionsController` | 1 | `clone`, `publish`, `diff`, `presentation`, `structure` |
| `ProjectsController` | 1 | список, створення, `clone`, `current-period` |
| `PeriodsController` | 3 | календар, `reopen` |
| `DocumentsController` | 1 | список, створення, читання, `validate`, `recalculate`, `submit`, `approve`, `reopen`, `export`, `import` |
| `RegistriesController` | 4 | реєстри, записи |
| `UnitsController` | 4 | список одиниць, `convert` |
| `MethodologiesController` | 4 | список, `publish`, `simulate` |
| `SecurityController` | 3 | ролі, користувачі |
| `AuditController` | 3 | історія змін комірок |
| `JobsController` | 5 | статус фонової задачі |
| `SourcesController` | 5 | каталог, `collect` |
| `ReportsController` | 5 | зрізи, `build` |

**Наскрізні вимоги до всіх контролерів:**

1. Пагінація обов'язкова; ендпоінтів «поверни все» не існує.
2. `limit > 500` → `400`.
3. Довгі операції (експорт, імпорт, перерахунок, збір) → `202 Accepted` з
   `jobId` і `statusUrl`.
4. Мутації, які можна повторити, приймають `Idempotency-Key`.
5. Жодного `DbContext` у контролері — перевіряється архітектурним тестом.
6. Перевірка прав — **тільки** через `IAccessDecisionService` у хендлері,
   ніколи не через `User.IsInRole` у контролері.
