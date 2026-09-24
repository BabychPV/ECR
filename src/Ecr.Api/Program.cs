using Ecr.Api.Auth;
using Ecr.Api.Errors;
using Ecr.Api.Middleware;
using Ecr.Api.Observability;
using Ecr.Api.Security;
using Ecr.Api.Startup;
using Ecr.Application;
using Ecr.Infrastructure;
using Ecr.Calculations;
using Ecr.Adapters.Excel;
using Ecr.Adapters.PiAf;
using Ecr.Adapters.Sql;
using Ecr.Api.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Net.Http.Headers;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ⚠ Без цього виклику процес не відповідає на старт/стоп SCM правильно і
// службу, зареєстровану інсталятором (docs/build/10-installer.md), не
// вдасться підняти. Поза Windows-службою — інертний no-op (перевірено,
// впливу на dev/тести/Linux CI немає): WindowsServiceHelpers.IsWindowsService()
// вмикає цю поведінку лише тоді, коли процес і справді піднятий SCM.
builder.Host.UseWindowsService();

// Персистентна конфігурація майданчика (НЕсекретні значення — Q-213):
// інсталятор кладе сюди копію appsettings.Production.json і більше НЕ
// перезаписує при оновленнях (NeverOverwrite, docs/build/10-installer.md,
// Folders.wxs: ConfigFolder) — на відміну від однойменного файлу поруч з
// Ecr.Api.exe, який кожне оновлення MSI перезаписує заповнювачем. Без
// цього рядка ця копія існувала на диску, але жодного налаштування з неї
// застосунок ніколи не читав — знайдено реальним прогоном на
// LenovoNakuLaptop. Логіка — в ProgramDataConfiguration (Ecr.Api.Startup),
// не тут: інакше не піддавалась би прямій перевірці без підняття хоста.
builder.Configuration.AddProgramDataConfig(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

// Конфігурація: змінні оточення з префіксом ECR_ перекривають файли
// (тому й секрети — лише сюди, ніколи в жоден із файлів вище, D-11).
builder.Configuration.AddEnvironmentVariables(prefix: "ECR_");

// Файловий журнал (`D14-09`): під Windows-службою консолі немає, і без файлу
// стек винятку з CorrelationId не зберігався ніде. Консоль і EventLog лишаються
// типовими постачальниками хоста — файл додається поруч, не замість.
builder.Logging.AddEcrFileLog();

builder.Services.AddEcrInfrastructure(builder.Configuration);
builder.Services.AddEcrCalculations();
builder.Services.AddExcelAdapters();
builder.Services.AddPiAfAdapters();
// ⚠ Окремим викликом, а не всередині AddPiAfAdapters: SQL-джерело (FLERT,
// ФВ-11.8) — не PI, і зібрати їх в одну реєстрацію означало б повторити в
// композиції рівно ту помилку, через яку не-PI джерело так довго не мало
// чим оголоситися.
builder.Services.AddSqlAdapters();
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
// BE-08: кореляція запиту доїжджає до itg.JobProgress через планувальник.
builder.Services.AddSingleton<Ecr.Application.Ports.ICorrelationIdAccessor,
    Ecr.Api.Middleware.HttpCorrelationIdAccessor>();
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
// ⚠ Те саме для затримки старту задач (ФВ-12.2): міст живе в
// Ecr.Infrastructure, метрика — тут. Без цього рядка ecr.job.start_latency
// не мала б жодного викликача, а `tz/08` §8.3 обіцяв би вимірювання, якого
// немає, — рівно та вада, яку 2026-09-18 знайшли в звільненнях від трасування.
builder.Services.AddSingleton<Ecr.Application.Ports.IJobStartMetrics,
    Ecr.Api.Observability.JobStartMetricsAdapter>();
builder.Services.AddScoped<Ecr.Api.Observability.BudgetMetricsFilter>();
builder.Services
    .AddControllers(options => options.Filters.Add<Ecr.Api.Observability.BudgetMetricsFilter>())
    // ⚠ Перелік конвертерів — в `EcrJsonSerialization`, а не тут: ті самі
    // правила потрібні двом незалежним наборам опцій (нижче), і копія в
    // кожному розійшлася б непомітно.
    .AddJsonOptions(options => Ecr.Api.Startup.EcrJsonSerialization.Configure(options.JsonSerializerOptions));

// ⚠ Ті самі налаштування — і для генератора OpenAPI. Він читає JSON-опції
// мінімальних API (`Microsoft.AspNetCore.Http.Json`), а не MVC: без цього
// рядка сервер віддавав би імена, а схема описувала б числа — і згенерований
// клієнт розходився б із дійсністю в протилежний бік.
builder.Services.ConfigureHttpJsonOptions(
    options => Ecr.Api.Startup.EcrJsonSerialization.Configure(options.SerializerOptions));
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
/*
 * Стиснення відповідей (`RD-01`, `DIRECTIVE-14-ARCH.md` §3.4).
 *
 * ⛔ Його не було взагалі: `AddResponseCompression` у `src` — нуль збігів. Зріз
 * 500×60 — це 0.8–3 МБ JSON, і він їхав мережею як є; SPA-бандл теж.
 *
 * ⚠ `EnableForHttps = true` — свідоме рішення, а не недогляд. Стиснення поверх
 * TLS відкриває BREACH лише тоді, коли тіло відповіді містить СЕКРЕТ і
 * водночас відбитий у ньому вміст запиту. Тут ні того, ні того: токена
 * підробки запиту в тілі немає (cookie `HttpOnly` + `SameSite=Strict`), а
 * відповіді — дані документів того, кому вони й так належать за грантом.
 * Тому вимикати стиснення на HTTPS означало б платити мегабайтами за загрозу,
 * якої в цій моделі немає.
 *
 * ⚠ Brotli на `Fastest`: на рівні за замовчуванням (`Optimal`) процесор
 * коштує більше, ніж економія на мережі всередині майданчика.
 */
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(
    options => options.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(
    options => options.Level = System.IO.Compression.CompressionLevel.Fastest);

// ⚠ Обмеження частоти на анонімні дорогі шляхи (`S-10`). Пакета не додано
// навмисно: `Microsoft.AspNetCore.RateLimiting` — частина спільного фреймворку,
// тобто нової залежності (і нового ліцензійного рядка) тут немає. Правило й
// пояснення — в `Security/LoginRateLimiting.cs`, а не тут: межа, розмазана між
// композицією і конвеєром, розходиться першою ж правкою.
builder.Services.AddEcrRateLimiting(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddCheck<Ecr.Api.Health.DatabaseHealthCheck>("db", tags: ["db", "ready"])
    .AddCheck<Ecr.Api.Health.JobsHealthCheck>("jobs", tags: ["ready"])
    .AddCheck<Ecr.Api.Health.SourcesHealthCheck>("sources", tags: ["ready"]);

var app = builder.Build();

// ⚠ ДО послідовності старту: якщо в теку журналу не вдається писати, про це
// треба сказати раніше, ніж старт упаде з іншої причини й пояснення не лишиться.
app.ReportFileLog();

// ⚠ ПОСЛІДОВНІСТЬ СТАРТУ (B01 §6.3) — порядок значущий:
// 1) retry-очікування БД  2) звірка міграцій  3) Validate/Migrate
// 4) ідемпотентний seed   5) валідація метаданих
// 6) прогрів кешу         7) перевірка запасу партицій
await app.RunEcrStartupSequenceAsync();

/*
 * ⛔ `ExceptionHandlingMiddleware` — НАЙЗОВНІШНІЙ (`S-23`). Доти зовні стояв
 * `CorrelationIdMiddleware`, тобто виняток, кинутий у ньому самому, не мав кому
 * перетворитися на `problem+json`: він виходив у хост, і клієнт отримував
 * обірване з'єднання або голий 500 без коду, без `correlationId` і без тіла —
 * рівно в тому випадку, коли пояснити причину найважче.
 *
 * ⚠ Обмін місцями нічого не забирає у кореляції: `ExceptionHandlingMiddleware`
 * читає ідентифікатор з `HttpContext.Items`, який `CorrelationIdMiddleware`
 * заповнює ПЕРШИМ рядком свого `InvokeAsync` — тобто до будь-якого можливого
 * кидка нижче по конвеєру.
 */
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();

// ⚠ Заголовки безпеки (`S-22`) — ВСЕРЕДИНІ обробника помилок, і це не суперечить
// «заголовки в кожній відповіді»: middleware ставить їх через `OnStarting`, який
// виконується після `Response.Clear()` обробника помилок. Пояснення — у файлі.
app.UseMiddleware<SecurityHeadersMiddleware>();

// ⚠ ПЕРЕД `UseStaticFiles`: інакше бандл і зріз їхали б нестисненими (`RD-01`).
app.UseResponseCompression();

/*
 * Заголовки кешу для SPA (`DAT-08`, серверна половина).
 *
 * ⛔ Їх не було, і це не питання швидкості, а **білого екрана**. Усі сторінки
 * клієнта — ліниві чанки з хешем у назві. Після оновлення MSI відкрита вкладка
 * просить чанк, якого на диску вже немає: `import()` відхиляється, і
 * застосунок зникає — невідрізнимо від дефекту продукту.
 *
 * Тому дві різні політики:
 *   `/assets/*` — ім'я містить хеш вмісту, отже файл незмінний назавжди;
 *   `index.html` (зокрема з фолбека нижче) — `no-cache`, бо саме він знає,
 *   які хеші чинні СЬОГОДНІ. Закешований `index.html` і був би тією вкладкою,
 *   що вічно просить старий чанк.
 *
 * ⚠ Клієнтська половина (`vite:preloadError` → «встановлено нову версію») —
 * окремо, у `src/Ecr.Web`: сервер не може знати, що в чужій вкладці відкрито.
 */
var staticFileOptions = new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var path = context.Context.Request.Path.Value ?? string.Empty;

        // ⚠ Заголовок пишеться рядком, а не через `CacheControlHeaderValue`:
        // директива `immutable` не має власної властивості в типізованому
        // заголовку, і через `Extensions` вона задається як пара «ім'я=значення»,
        // тобто не тим, чим є. Рядок тут — точніший, а не лінивіший.
        context.Context.Response.Headers[HeaderNames.CacheControl] =
            path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
                ? "public, max-age=31536000, immutable"
                : "no-cache";
    },
};

// ⚠ Веб-клієнт (src/Ecr.Web, збирається в wwwroot інсталятором —
// tools/build-msi.ps1) НЕ вимагає автентифікації сам по собі: сторінку
// логіну (index.html, JS/CSS) має бути можливо завантажити ДО логіну,
// інакше завантажити її неможливо взагалі. API нижче захищене окремо,
// незалежно від цього виклику. У dev/тестах wwwroot не існує — middleware
// просто нічого не знаходить, без винятку.
app.UseStaticFiles(staticFileOptions);

app.UseAuthentication();
app.UseMiddleware<SecurityStampMiddleware>();   // після автентифікації, до авторизації
app.UseMiddleware<PasswordChangeMiddleware>();   // разовий пароль закриває все, крім його зміни
app.UseMiddleware<SimulationReadOnlyMiddleware>(); // симуляція «очима користувача» — лише читання (V-06)
app.UseAuthorization();

// ⚠ Обмежувач — ПІСЛЯ автентифікації й авторизації: межа пошуку (BE-19)
// ділиться за КОРИСТУВАЧЕМ, а до `UseAuthentication` його ще немає; анонімний
// запит до пошуку отримує 401 і межі не витрачає. Вхід (`S-10`) від цього не
// дорожчає: без cookie автентифікація — перевірки в пам'яті, PBKDF2 не почато.
app.UseRateLimiter();

app.MapControllers();

// ⛔ Q-247: без цієї перевірки схема OpenAPI (усі маршрути, усі DTO —
// включно з полями на кшталт CreateUserRequest.InitialPassword) і
// Scalar-переглядач були доступні АНОНІМНО на будь-якому майданчику,
// не лише в розробці — розвідка поверхні атаки без жодного тертя. На
// відміну від /health/db (Q-221), живий перегляд схеми для вже
// автентифікованих людей у проді не має практичної цінності — тому тут
// не .RequireAuthorization(), а гейт середовища: той самий підхід, який
// типово має шаблон ASP.NET (Swagger/Scalar — інструмент розробника, не
// проду), і найпростіший спосіб узагалі прибрати цю поверхню там, де
// вона не потрібна.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// /health/live — лише «процес живий». Жодної перевірки: будь-яке звернення
// до БД тут перетворило б перезапуск процесу на наслідок проблем із базою.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponse.WriteReadyAsync,
});

// /health/db віддає ПОДРОБИЦІ: режим редакції, RCSI, файлові групи, запас
// партицій і перелік того, що в цьому режимі недоступне (АРХ-7 п. 5).
// ⛔ Q-221: на відміну від /health/live й /health/ready (моніторинг/SCM,
// анонімний доступ — навмисно), цей ендпоінт розкриває внутрішню будову
// бази будь-якому, хто дістанеться порту. Споживач — адмінська сторінка
// HealthPage.tsx: фронтенд ховає пункт меню від неавторизованих, але без
// .RequireAuthorization() тут це лише візуальна, не справжня межа.
app.MapHealthChecks("/health/db", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("db"),
    ResponseWriter = HealthResponse.WriteAsync,
}).RequireAuthorization();

// ⚠ Явний 404 для api/health/openapi/scalar ПЕРЕД загальним SPA-фолбеком
// — обов'язково, інакше помилковий запит на неіснуючий `/api/v1/typo`
// отримав би 200 з index.html замість чіткого 404 (контракт
// ECR-DOC-0404) — класична пастка спільного хостингу SPA+API. Це, а не
// один regex на catch-all: конструкція `{*path:nonfile:regex(...)}` на
// ПОРОЖНЬОМУ залишку шляху (сам корінь "/") ламала фолбек узагалі —
// перевірено реальним прогоном проти справжнього `dist` (Q-214). Ці
// маршрути — MapFallback, тому програють будь-якому реальному
// контролеру/health-check вище, і водночас точніші за загальний
// `{*path}` нижче, тож саме вони спрацьовують на цих префіксах.
app.MapFallback("/api/{**_}", () => Results.NotFound());
app.MapFallback("/health/{**_}", () => Results.NotFound());
app.MapFallback("/openapi/{**_}", () => Results.NotFound());
app.MapFallback("/scalar/{**_}", () => Results.NotFound());

// Усе інше (включно з коренем "/") — SPA-шелл; `:nonfile` — запит на
// реально відсутній статичний файл (наприклад, видалену картинку) так
// само лишається 404 від UseStaticFiles вище, а не підміняється
// сторінкою застосунку.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Точка входу; <c>public partial</c> — щоб тести могли підняти застосунок.</summary>
public partial class Program;
