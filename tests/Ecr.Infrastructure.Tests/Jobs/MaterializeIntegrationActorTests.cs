// tests/Ecr.Infrastructure.Tests/Jobs/MaterializeIntegrationActorTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Матеріалізація PI пише в комірки від імені <c>svc-integration</c> — наскрізно,
/// без HTTP-запиту, як у задачі Quartz.
/// </summary>
/// <remarks>
/// ⛔ P0. Сід заводив <c>svc-integration</c>, але задача не входила в
/// <see cref="JobActorScope"/>: <c>PatchCellsHandler</c> бачив
/// <c>UserId = null</c> і відмовляв <c>ECR-AUTH-0401</c>. Жоден тест цього не
/// бачив — реальний <c>IntegrationCellPatcher</c> із реальним обробником запису
/// не ганявся ніде (решта тестів задачі — на підробленому патчері).
///
/// ⚠ Тому тут усе справжнє й зібране КОНТЕЙНЕРОМ, як у проді:
/// <c>AddEcrApplication</c> + <c>AddEcrInfrastructure</c>, задача береться за
/// маркером <see cref="IMaterializeCollectedDataJob"/> (саме ним її ставить
/// <c>CollectionJob</c>), а <see cref="ICurrentUser"/> — та сама обгортка
/// <see cref="JobAwareCurrentUser"/>, що в <c>Program.cs</c>, поверх
/// користувача «поза запитом» (<see cref="NoHttpRequestUser"/> повторює
/// <c>Ecr.Api.Auth.CurrentUser</c> без <c>HttpContext</c>).
///
/// ⚠ Запис у НАЯВНИЙ рядок (фіксовані рядки є з відкриття періоду; і кожен
/// повторний прогін) — основний випадок, а не виняток: патчер шле версію
/// рядка, а не <c>null</c> «створити». Гонки з людиною між читанням і записом
/// відтворюються перехоплювачем <see cref="IRowStore"/> — справжнє сховище, у
/// яке вставлено дію ПІСЛЯ читання версій.
/// </remarks>
[Collection("SqlServer")]
public sealed class MaterializeIntegrationActorTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MidJanuary = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-actor")]
    public async Task Задача_без_HTTP_пише_у_наявний_фіксований_рядок_від_імені_svc_integration()
    {
        // ⚠ Таблиця `Fixed`, рядок заведено будівником — як при відкритті періоду.
        var stand = await ArrangeAsync(TableRowMode.Fixed, newRow: false);

        try
        {
            await using var provider = BuildProvider(new RowStoreHook());

            await using (var scope = provider.CreateAsyncScope())
            {
                await RunJobAsync(scope, stand);

                // ⚠ Після задачі scope знову «поза задачею»: автор не протікає в
                // наступну роботу того самого scope.
                Assert.Null(scope.ServiceProvider.GetRequiredService<JobActorScope>().Current);
            }
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ (1): прибрати вхід у scope з `MaterializeCollectedDataJob`
        // → задача падає `ECR-AUTH-0401` «Анонімний запит не може змінювати дані».
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ (2): `BaseVersion: null` у `IntegrationCellPatcher`
        // для наявного рядка → `ECR-ROW-0409` «Рядки з такими ключами вже існують».
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ (3): `scope.Enter` замість `scope.EnterIntegration`
        // в `IntegrationActor` → `ECR-DOC-0404`: грантів у svc-integration немає.
        Assert.Equal(7m, Assert.IsType<decimal>(await CellAsync(stand, stand.ColumnDefIds[0])));
        Assert.Equal($"{stand.SvcId}|Integration", await LastChangeAsync(stand, stand.ColumnDefIds[0]));

        // (а) Записано БЕЗ жодного гранта: у svc-integration немає ні ролі, ні
        // призначення — право дає контекст задачі, а не адміністрування.
        Assert.Equal(0, await SvcAssignmentsAsync(stand.SvcId));
    }

    /// <summary>
    /// (б) Той самий <c>svc-integration</c> поза задачею інтеграції права
    /// запису без грантів НЕ має — ні з HTTP-запиту, ні з задачі людини.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>AccessDecisionService.BuildProfileAsync</c>
    /// замінити <c>currentUser.IsIntegrationJob</c> перевіркою ІМЕНІ
    /// (<c>svc-integration</c>) → обидва варіанти пишуть, тест червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-writer")]
    [InlineData("http")]
    [InlineData("human-job")]
    public async Task Svc_integration_поза_задачею_інтеграції_не_пише_без_грантів(string context)
    {
        var stand = await ArrangeAsync(TableRowMode.Fixed, newRow: false);

        try
        {
            // HTTP: користувач ЗАПИТУ — svc-integration (ніби cookie), задачі немає.
            // Задача людини: svc-integration автор через звичайний `Enter`.
            ICurrentUser request = context == "http" ? new HttpRequestUser(stand.SvcId) : new NoHttpRequestUser();
            await using var provider = BuildProvider(new RowStoreHook(), request);
            await using var scope = provider.CreateAsyncScope();

            using var author = context == "http"
                ? null
                : scope.ServiceProvider.GetRequiredService<JobActorScope>()
                    .Enter(new JobActor(stand.SvcId, IntegrationActor.UserName, "en", [], Guid.NewGuid().ToString("N")));

            var rows = await scope.ServiceProvider.GetRequiredService<IRowStore>()
                .GetRowsAsync(stand.Chain.TableInstanceId, stand.Chain.PeriodKey, CancellationToken.None);
            var version = rows.Single(r => r.RowKey == stand.RowKey).RowVersion;

            var error = await Assert.ThrowsAsync<Ecr.Application.Errors.NotFoundException>(
                () => scope.ServiceProvider.GetRequiredService<PatchCellsHandler>().HandleAsync(
                    new PatchCellsRequest(
                        stand.Chain.TableInstanceId, stand.Chain.PeriodKey.Value, CellChangeOrigins.UserEdit,
                        [new PatchRow(stand.RowKey, version, [new PatchCell(stand.ColumnCodes[0], 5m)])]),
                    CancellationToken.None));

            // Та сама відповідь, що будь-кому без гранта: документа «немає».
            Assert.Equal("ECR-DOC-0404", error.ErrorCode);
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        Assert.Null(await CellAsync(stand, stand.ColumnDefIds[0]));
        Assert.Null(await LastChangeAsync(stand, stand.ColumnDefIds[0]));
    }

    /// <summary>(д) Комірку, яку людина правила ДО прогону, інтеграція не переписує.</summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати відсіювання <c>manual</c> у
    /// <c>IntegrationCellPatcher.PlanAsync</c> → у комірці 7 від svc-integration.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-118")]
    public async Task Правка_людини_до_прогону_лишається_а_в_журналі_покриття_рядок()
    {
        var stand = await ArrangeAsync(TableRowMode.Fixed, newRow: false);
        var humanId = await AddHumanAsync(stand.RoleId);

        try
        {
            await using var provider = BuildProvider(new RowStoreHook());
            await HumanEditAsync(provider, stand, humanId, 42m);

            await using var scope = provider.CreateAsyncScope();
            await RunJobAsync(scope, stand);
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        Assert.Equal(42m, Assert.IsType<decimal>(await CellAsync(stand, stand.ColumnDefIds[0])));
        Assert.Equal($"{humanId}|UserEdit", await LastChangeAsync(stand, stand.ColumnDefIds[0]));

        var coverage = Assert.Single(await CoverageAsync(stand));
        Assert.Equal(CollectionCoverage.ConflictKeptManual, coverage.Status);
    }

    /// <summary>
    /// Комірку під правилом «дозволено з підтвердженням» (<c>ФВ-2.16</c>)
    /// інтеграція не пише: підтвердження — дія людини.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати фільтр <c>AwaitingConfirmationAsync</c> у
    /// <c>IntegrationCellPatcher</c> → комірка записана (обробник запису
    /// підтвердження поки не вимагає — це паралельна робота над ФВ-2.16), тобто
    /// інтеграція «підтвердила» сама.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Комірку_що_вимагає_підтвердження_інтеграція_не_пише_а_журналює()
    {
        var stand = await ArrangeAsync(TableRowMode.Fixed, newRow: false);

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            db.PeriodAccessRules.Add(
                PeriodAccessRuleDef
                    .AlwaysReadOnly(stand.Chain.TemplateVersionId, OutOfWindowBehavior.AllowWithConfirmation)
                    .ForTable(stand.Chain.TableDefId));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            await using var provider = BuildProvider(new RowStoreHook());
            await using var scope = provider.CreateAsyncScope();
            await RunJobAsync(scope, stand);
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        Assert.Null(await CellAsync(stand, stand.ColumnDefIds[0]));
        Assert.Null(await LastChangeAsync(stand, stand.ColumnDefIds[0]));

        // ⛔ Власний статус, а не `ConflictKeptManual` «має правку людини»:
        // людина комірку не правила.
        // МУТАЦІЙНИЙ ДОКАЗ: у `MaterializeCollectedDataJob` журналювати
        // `AwaitingConfirmation` статусом `ConflictKeptManual` (як доти) → червоний.
        var coverage = Assert.Single(await CoverageAsync(stand));
        Assert.Equal(CollectionCoverage.SkippedNeedsConfirmation, coverage.Status);
        Assert.Contains($"{stand.RowKey}:{stand.ColumnCodes[0]}", coverage.Details, StringComparison.Ordinal);
        Assert.Contains("підтвердження", coverage.Details, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-actor")]
    public async Task Відсутній_рядок_таблиці_Mixed_і_далі_створюється()
    {
        var stand = await ArrangeAsync(TableRowMode.Mixed, newRow: true);

        try
        {
            await using var provider = BuildProvider(new RowStoreHook());
            await using var scope = provider.CreateAsyncScope();
            await RunJobAsync(scope, stand);
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        // Контроль: `null` лишився для рядка, якого немає (R-B2 «створити»).
        Assert.Equal(7m, Assert.IsType<decimal>(await CellAsync(stand, stand.ColumnDefIds[0])));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-actor")]
    public async Task Повторний_прогін_тих_самих_точок_не_дає_ні_нової_версії_рядка_ні_аудиту()
    {
        var stand = await ArrangeAsync(TableRowMode.Fixed, newRow: false);

        try
        {
            await using var provider = BuildProvider(new RowStoreHook());

            await using (var scope = provider.CreateAsyncScope())
            {
                await RunJobAsync(scope, stand);
            }

            var versionAfterFirst = await RowVersionAsync(stand);
            var auditAfterFirst = await AuditCountAsync(stand);
            Assert.Equal(1, auditAfterFirst);

            await using (var scope = provider.CreateAsyncScope())
            {
                await RunJobAsync(scope, stand);
            }

            // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати відсіювання незмінних значень у
            // `IntegrationCellPatcher` → обробник пише ту саму комірку вдруге:
            // аудиту не додається (`U-22`), але версія рядка росте — нова
            // ревізія на кожен прогін, у якому нічого не змінилося.
            Assert.Equal(versionAfterFirst, await RowVersionAsync(stand));
            Assert.Equal(auditAfterFirst, await AuditCountAsync(stand));
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-118")]
    public async Task Правка_людини_між_читанням_і_записом_лишається_а_решта_рядка_записана()
    {
        // Дві комірки одного рядка: людина правитиме першу, другу — ні.
        var stand = await ArrangeAsync(TableRowMode.Fixed, newRow: false, twoColumns: true);
        var humanId = await AddHumanAsync(stand.RoleId);

        try
        {
            var hook = new RowStoreHook();
            await using var provider = BuildProvider(hook);

            // ⚠ Правка людини — СПРАВЖНІЙ запис через `PatchCellsHandler` з
            // `UserEdit`, у своєму scope, одразу ПІСЛЯ того, як патчер прочитав
            // версії рядків: саме те вікно, у якому інтеграція інакше затерла б її.
            hook.AfterRead = async () =>
            {
                hook.AfterRead = null;
                await HumanEditAsync(provider, stand, humanId, 42m);
            };

            await using var scope = provider.CreateAsyncScope();
            await RunJobAsync(scope, stand);
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати повтор у `IntegrationCellPatcher`
        // (перша ж `ECR-CELL-0409` — виняток) → прогін падає, друга комірка
        // не записана, рядка в журналі покриття немає.
        Assert.Equal(42m, Assert.IsType<decimal>(await CellAsync(stand, stand.ColumnDefIds[0])));
        Assert.Equal($"{humanId}|UserEdit", await LastChangeAsync(stand, stand.ColumnDefIds[0]));

        Assert.Equal(7m, Assert.IsType<decimal>(await CellAsync(stand, stand.ColumnDefIds[1])));
        Assert.Equal($"{stand.SvcId}|Integration", await LastChangeAsync(stand, stand.ColumnDefIds[1]));

        // Контроль: справжня правка людини лишається `ConflictKeptManual`, а не
        // `SkippedWriteConflict` — хоч і прийшла через той самий `ECR-CELL-0409`.
        var coverage = Assert.Single(await CoverageAsync(stand));
        Assert.Equal(CollectionCoverage.ConflictKeptManual, coverage.Status);
        Assert.Contains($"{stand.RowKey}:{stand.ColumnCodes[0]}", coverage.Details, StringComparison.Ordinal);
        Assert.Contains("правку людини", coverage.Details, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-118")]
    public async Task Постійний_конфлікт_версії_три_спроби_рядок_у_журналі_прогін_завершується()
    {
        var stand = await ArrangeAsync(TableRowMode.Fixed, newRow: false);

        var hook = new RowStoreHook();

        // Хтось інший піднімає версію рядка після КОЖНОГО читання — конфлікт,
        // який перечитування не знімає.
        hook.AfterRead = () => BumpRowAsync(stand);

        try
        {
            await using var provider = BuildProvider(hook);
            await using var scope = provider.CreateAsyncScope();

            // ⛔ Прогін НЕ падає: виняток тут означав би, що один «гарячий»
            // рядок зупиняє перенесення всієї сутності.
            await RunJobAsync(scope, stand);
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        // ⚠ Кожна спроба читає рядки двічі: патчер (план) і обробник (контекст).
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати повтор → одна спроба (2 читання) і
        // виняток; прибрати стелю → перехоплювач зупиняє нескінченний цикл.
        Assert.Equal(2 * IntegrationCellPatcher.MaxAttempts, hook.Reads);

        Assert.Null(await CellAsync(stand, stand.ColumnDefIds[0]));
        Assert.Null(await LastChangeAsync(stand, stand.ColumnDefIds[0]));

        // ⛔ `SkippedWriteConflict`, а НЕ `ConflictKeptManual` «має правку
        // людини»: людина комірку не правила, рядок лише змінювали під час запису.
        // МУТАЦІЙНИЙ ДОКАЗ: у `IntegrationCellPatcher` повернути вичерпані
        // повтори в `KeptManual` (як доти) → статус `ConflictKeptManual`, червоний.
        var coverage = Assert.Single(await CoverageAsync(stand));
        Assert.Equal(CollectionCoverage.SkippedWriteConflict, coverage.Status);
        Assert.Contains($"{stand.RowKey}:{stand.ColumnCodes[0]}", coverage.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("правку людини", coverage.Details, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-actor")]
    public async Task Без_запису_svc_integration_задача_відмовляє_з_кодом_а_не_пише_від_нікого()
    {
        // ⚠ Відсутність запису імітується ВИМКНЕННЯМ у транзакції, яка
        // відкочується: видалити чи вимкнути рядок `sec.User` у спільній
        // тестовій базі означало б зламати сусідні тести. Контекст — без
        // стратегії повторів (як у будівника): з нею EF не дає власної транзакції.
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE sec.[User] SET IsActive = 0 WHERE UserName = N'svc-integration'");

        var scope = new JobActorScope();
        var error = await Assert.ThrowsAsync<Ecr.Application.Errors.NotFoundException>(
            () => new IntegrationActor(db, scope).EnterAsync(CancellationToken.None));

        await tx.RollbackAsync();

        // ⛔ Код, а не мовчазний запис «від нікого»: без цієї відмови задача
        // дійшла б до `PatchCellsHandler` анонімною і впала б `ECR-AUTH-0401`,
        // яка каже «увійдіть» тому, кому входити нікуди.
        Assert.Equal("ECR-SEC-0404", error.ErrorCode);
        Assert.Contains("svc-integration", error.Message, StringComparison.Ordinal);
        Assert.Null(scope.Current);
    }

    /// <summary>Усе, що заведено для одного прогону.</summary>
    private sealed record Stand(
        TestDocument Chain,
        int EntityId,
        int SvcId,
        int RoleId,
        string RowKey,
        IReadOnlyList<int> ColumnDefIds,
        IReadOnlyList<string> ColumnCodes);

    /// <summary>
    /// Ланцюг із відкритим періодом, сутність із мапінгами в рядок і роль із
    /// грантом <c>Write</c> на проєкт — для ЛЮДЕЙ тестів, не для <c>svc-integration</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>svc-integration</c> гранта НЕ отримує: право запису інтеграції дає
    /// контекст задачі (<c>JobActorScope.EnterIntegration</c> →
    /// <c>AccessProfile.IsIntegrationWriter</c>). Доти тест тимчасово
    /// призначав йому цю роль — без неї задача падала на <c>ECR-DOC-0404</c>.
    /// Роль знімається у <c>finally</c> кожного тесту: база спільна для всієї колекції.
    /// </remarks>
    private async Task<Stand> ArrangeAsync(TableRowMode rowMode, bool newRow, bool twoColumns = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(rowMode: rowMode, ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        var rowKey = newRow
            ? $"PI_{Guid.NewGuid():N}"[..20]
            : (await db.TableRows
                .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
                .Select(r => r.RowKey)
                .SingleAsync()).Value;

        int[] columnIds = twoColumns ? [chain.ColumnDefIds[1], chain.ColumnDefIds[2]] : [chain.ColumnDefIds[1]];
        var codes = new List<string>();
        foreach (var id in columnIds)
        {
            codes.Add(await db.ColumnDefs.Where(c => c.Id == id).Select(c => c.Code).SingleAsync());
        }

        var entityId = await ArrangeMappingAsync(db, rowKey, columnIds);
        var svcId = await SvcIntegrationIdAsync();
        var roleId = await CreateWriterRoleAsync(db, chain.ProjectId);

        return new Stand(chain, entityId, svcId, roleId, rowKey, columnIds, codes);
    }

    /// <summary>Прогін задачі в scope, як його ставить <c>CollectionJob</c>.</summary>
    private static async Task RunJobAsync(AsyncServiceScope scope, Stand stand)
    {
        var job = scope.ServiceProvider.GetRequiredService<IMaterializeCollectedDataJob>();

        await job.ExecuteAsync(
            new MaterializeTask(
                stand.EntityId, stand.Chain.ProjectId, stand.Chain.DocumentId, stand.Chain.TableInstanceId,
                stand.Chain.PeriodKey.Value,
                new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);
    }

    /// <summary>Контейнер як у проді: застосунок + інфраструктура + обгортка автора задачі.</summary>
    /// <param name="hook">Перехоплювач читань версій рядків (без дії — прозорий).</param>
    /// <param name="request">Користувач «запиту»; за замовчуванням — поза запитом.</param>
    private ServiceProvider BuildProvider(RowStoreHook hook, ICurrentUser? request = null)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);

        // Те саме, що `Program.cs` (F-01): автор задачі поверх користувача запиту.
        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            request ?? new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));

        // ⚠ Справжнє сховище рядків, обгорнуте перехоплювачем: і патчер, і
        // обробник отримують ТОЙ САМИЙ `RowStore`, лише з дією після читання.
        services.AddScoped<RowStore>();
        services.AddScoped<IRowStore>(sp => new InterceptingRowStore(sp.GetRequiredService<RowStore>(), hook));

        // ⚠ Черга перерахунку — підробка. Quartz тримає планувальник у
        // ГЛОБАЛЬНОМУ (на процес) репозиторії за ім'ям: другий контейнер у тому
        // самому процесі отримує вже звільнений планувальник першого, і запис
        // падає `ObjectDisposedException` на постановці перерахунку — після
        // коміту, тобто не про предмет цих тестів.
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider();
    }

    /// <summary>Правка людини через той самий обробник, у власному scope.</summary>
    private static async Task HumanEditAsync(ServiceProvider provider, Stand stand, int humanId, decimal value)
    {
        await using var scope = provider.CreateAsyncScope();
        using var author = scope.ServiceProvider.GetRequiredService<JobActorScope>()
            .Enter(new JobActor(humanId, "human", "en", [], Guid.NewGuid().ToString("N")));

        var rows = await scope.ServiceProvider.GetRequiredService<IRowStore>()
            .GetRowsAsync(stand.Chain.TableInstanceId, stand.Chain.PeriodKey, CancellationToken.None);
        var version = rows.Single(r => r.RowKey == stand.RowKey).RowVersion;

        await scope.ServiceProvider.GetRequiredService<PatchCellsHandler>().HandleAsync(
            new PatchCellsRequest(
                stand.Chain.TableInstanceId, stand.Chain.PeriodKey.Value, CellChangeOrigins.UserEdit,
                [new PatchRow(stand.RowKey, version, [new PatchCell(stand.ColumnCodes[0], value)])]),
            CancellationToken.None);
    }

    /// <summary>Чужий запис у рядок: будь-який <c>UPDATE</c> змінює <c>rowversion</c>.</summary>
    private async Task BumpRowAsync(Stand stand)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() "
            + $"WHERE PeriodKey = {stand.Chain.PeriodKey.Value} AND TableInstanceId = {stand.Chain.TableInstanceId} AND RowKey = N'{stand.RowKey}'";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Сутність із матеріалізованими мапінгами в рядок: на кожну колонку — своє
    /// поле з двома точками посеред січня (3 + 4, згортка <c>Sum</c> = 7).
    /// </summary>
    private static async Task<int> ArrangeMappingAsync(EcrDbContext db, string rowKey, int[] columnDefIds)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("P0 actor"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивна навмисно (як у `MaterializeMappingScopeTests`): активна
        // сутність без завершеного збору робить `SourcesHealthCheck` жовтим.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var points = new List<SourceDataPoint>();
        for (var i = 0; i < columnDefIds.Length; i++)
        {
            var field = $"F{i}_{tag}";
            var map = EntityFieldMap.ToColumn(entity.Id, field, columnDefIds[i]);
            map.SetMaterialization(rowKey, AggregationKind.Sum);
            db.EntityFieldMaps.Add(map);

            points.Add(new SourceDataPoint(field, MidJanuary, 3m, null, null, "Good"));
            points.Add(new SourceDataPoint(field, MidJanuary.AddHours(1), 4m, null, null, "Good"));
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var store = new CollectionStore(db, new TestClock(Now));
        var runId = await store.StartRunAsync(
            entity.Id, MidJanuary, MidJanuary.AddHours(2), isCatchUp: false, triggeredByUserId: null, CancellationToken.None);
        await store.UpsertRawPointsAsync(runId, entity.Id, points, CancellationToken.None);

        return entity.Id;
    }

    /// <summary>Тестова роль із грантом <c>Write</c> на проєкт — без жодного призначення.</summary>
    private static async Task<int> CreateWriterRoleAsync(EcrDbContext db, int projectId)
    {
        var role = new Role(
            EcrCode.Create($"P0SVC{Guid.NewGuid():N}"[..16]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "P0 human writer (test)" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync(CancellationToken.None);

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync(CancellationToken.None);

        return role.Id;
    }

    /// <summary>Людина з тією самою тестовою роллю (знімається разом із нею).</summary>
    private async Task<int> AddHumanAsync(int roleId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var human = new User($"human_{Guid.NewGuid():N}"[..20], "Test human", AuthProvider.Local);
        human.SetPassword("not-a-real-hash"); // CK_User_Provider: локальному — хеш.
        db.Users.Add(human);
        await db.SaveChangesAsync(CancellationToken.None);

        db.RoleAssignments.Add(new RoleAssignment(roleId, human.Id, principalSid: null));
        await db.SaveChangesAsync(CancellationToken.None);

        return human.Id;
    }

    private async Task RevokeAsync(int roleId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DELETE FROM sec.ResourceGrant WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.RoleAssignment WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.Role WHERE Id = {roleId};";
        await command.ExecuteNonQueryAsync();
    }

    private Task<object?> CellAsync(Stand stand, int columnDefId)
        => ScalarAsync(
            $"SELECT v.ValueNumeric FROM doc.CellValue v JOIN doc.TableRow r ON r.PeriodKey = v.PeriodKey AND r.Id = v.TableRowId "
            + $"WHERE r.TableInstanceId = {stand.Chain.TableInstanceId} AND r.RowKey = N'{stand.RowKey}' AND v.ColumnDefId = {columnDefId}");

    private Task<object?> LastChangeAsync(Stand stand, int columnDefId)
        => ScalarAsync(
            $"SELECT TOP 1 CONCAT(ChangedByUserId, N'|', Origin) FROM aud.CellChange "
            + $"WHERE DocumentId = {stand.Chain.DocumentId} AND RowKey = N'{stand.RowKey}' AND ColumnDefId = {columnDefId} ORDER BY Id DESC");

    private async Task<int> AuditCountAsync(Stand stand)
        => Convert.ToInt32(
            await ScalarAsync($"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {stand.Chain.DocumentId} AND RowKey = N'{stand.RowKey}'"),
            System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> RowVersionAsync(Stand stand)
        => Convert.ToBase64String((byte[])(await ScalarAsync(
            $"SELECT RowVersion FROM doc.TableRow WHERE PeriodKey = {stand.Chain.PeriodKey.Value} "
            + $"AND TableInstanceId = {stand.Chain.TableInstanceId} AND RowKey = N'{stand.RowKey}'"))!);

    private async Task<List<(string Status, string Details)>> CoverageAsync(Stand stand)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT Status, Details FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} AND Status IS NOT NULL";
        await using var reader = await command.ExecuteReaderAsync();

        var result = new List<(string, string)>();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private async Task<int> SvcAssignmentsAsync(int svcId)
        => Convert.ToInt32(
            await ScalarAsync($"SELECT COUNT(*) FROM sec.RoleAssignment WHERE UserId = {svcId}"),
            System.Globalization.CultureInfo.InvariantCulture);

    private async Task<int> SvcIntegrationIdAsync()
        => Convert.ToInt32(
            await ScalarAsync($"SELECT Id FROM sec.[User] WHERE UserName = N'{IntegrationActor.UserName}'"),
            System.Globalization.CultureInfo.InvariantCulture);

    private async Task<object?> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Дія після читання рядків і лічильник читань поза нею.</summary>
    private sealed class RowStoreHook
    {
        /// <summary>Стеля читань: нескінченний повтор має впасти тут, а не зависнути.</summary>
        private const int MaxReads = 20;

        private bool _inside;

        /// <summary>Дія після кожного читання версій; <c>null</c> — прозоро.</summary>
        public Func<Task>? AfterRead { get; set; }

        /// <summary>Скільки разів рядки читали поза самою дією.</summary>
        public int Reads { get; private set; }

        public async Task OnReadAsync()
        {
            if (_inside)
            {
                return;
            }

            Reads++;
            if (Reads > MaxReads)
            {
                throw new InvalidOperationException($"Понад {MaxReads} читань рядків: повтор без стелі.");
            }

            if (AfterRead is { } action)
            {
                _inside = true;
                try
                {
                    await action();
                }
                finally
                {
                    _inside = false;
                }
            }
        }
    }

    /// <summary>Справжнє сховище рядків із дією ПІСЛЯ <see cref="IRowStore.GetRowsAsync"/>.</summary>
    private sealed class InterceptingRowStore(IRowStore inner, RowStoreHook hook) : IRowStore
    {
        public async Task<IReadOnlyList<RowState>> GetRowsAsync(long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
        {
            var rows = await inner.GetRowsAsync(tableInstanceId, periodKey, ct);
            await hook.OnReadAsync();
            return rows;
        }

        public Task<TableInstanceRef> ResolveTableInstanceAsync(long tableInstanceId, CancellationToken ct)
            => inner.ResolveTableInstanceAsync(tableInstanceId, ct);

        public Task<IReadOnlyDictionary<long, TableInstanceRef>> ResolveTableInstancesAsync(
            IReadOnlyCollection<long> tableInstanceIds, CancellationToken ct)
            => inner.ResolveTableInstancesAsync(tableInstanceIds, ct);

        public Task<IReadOnlyDictionary<long, IReadOnlyList<RowState>>> GetRowsBatchAsync(
            IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
            => inner.GetRowsBatchAsync(tableInstanceIds, periodKey, ct);

        public Task<IReadOnlyDictionary<string, string>> GetRowVersionsAsync(long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
            => inner.GetRowVersionsAsync(tableInstanceId, periodKey, ct);

        public Task<IReadOnlyDictionary<string, long>> GetRowIdsAsync(long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
            => inner.GetRowIdsAsync(tableInstanceId, periodKey, ct);

        public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>> GetRowIdsBatchAsync(
            IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
            => inner.GetRowIdsBatchAsync(tableInstanceIds, periodKey, ct);

        public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, string>>> GetRowVersionsBatchAsync(
            IReadOnlyList<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
            => inner.GetRowVersionsBatchAsync(tableInstanceIds, periodKey, ct);

        public Task<long> CreateRowAsync(long tableInstanceId, PeriodKey periodKey, RowKey rowKey, int ordinal, CancellationToken ct)
            => inner.CreateRowAsync(tableInstanceId, periodKey, rowKey, ordinal, ct);

        public Task<IReadOnlyList<long>> CreateRowsAsync(
            long tableInstanceId, PeriodKey periodKey, IReadOnlyList<RowKey> rowKeys, int ordinal, CancellationToken ct)
            => inner.CreateRowsAsync(tableInstanceId, periodKey, rowKeys, ordinal, ct);

        public Task<IReadOnlyList<IReadOnlyList<long>>> CreateRowsBatchAsync(
            IReadOnlyList<RowCreationBatch> batches, CancellationToken ct)
            => inner.CreateRowsBatchAsync(batches, ct);

        public Task TouchRowsAsync(IReadOnlyList<long> rowIds, PeriodKey periodKey, DateTime utcNow, CancellationToken ct)
            => inner.TouchRowsAsync(rowIds, periodKey, utcNow, ct);

        public Task<IReadOnlyDictionary<long, bool>> GetOrphanFlagsAsync(long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
            => inner.GetOrphanFlagsAsync(tableInstanceId, periodKey, ct);

        public Task<IReadOnlyList<TableInstanceRef>> GetTableInstancesAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
            => inner.GetTableInstancesAsync(documentId, periodKey, ct);

        public Task<int> EnsureTableInstancesAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
            => inner.EnsureTableInstancesAsync(documentId, periodKey, ct);

        public Task<IReadOnlyList<long>> GetOrphanedRowIdsAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
            => inner.GetOrphanedRowIdsAsync(documentId, periodKey, ct);
    }

    /// <summary>
    /// <c>Ecr.Api.Auth.CurrentUser</c> поза HTTP-запитом: анонімний, а
    /// кореляції немає зовсім (там — той самий виняток).
    /// </summary>
    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }

    /// <summary>
    /// <c>Ecr.Api.Auth.CurrentUser</c> у запиті з cookie заданого користувача.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>IsIntegrationJob</c> НЕ перевизначено — як і в справжньому: HTTP-запит
    /// цієї ознаки не має.
    /// </remarks>
    private sealed class HttpRequestUser(int userId) : ICurrentUser
    {
        public int? UserId => userId;

        public string? UserName => IntegrationActor.UserName;

        public string CorrelationId => "http-request";

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
