using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Доступ до вивантаження документа (<c>ФВ-6.13</c>).
/// </summary>
/// <remarks>
/// ⛔ Експорт віддає документ ЦІЛКОМ. Функціональне право
/// <c>Document.Export</c> має роль <c>DataEntry</c> — тобто майже кожен, хто
/// працює з системою; воно каже «цей користувач узагалі вивантажує
/// документи», а не «саме цей документ». Другу відповідь дає грант, і до
/// <c>A7-55</c> її ніхто не питав.
/// </remarks>
// ⛔ Трейт `ФВ-6.13` знятий (директива №09 §8.2). Вимога каже про
// ВПОРЯДКОВАНІ рівні доступу (`None` → `Read` → … → `Manage`, вищий
// включає нижчі), а тут рішення про доступ віддає ЗАГЛУШКА
// (`Substitute.For<IAccessDecisionService>`), яка повертає те, що їй
// сказали. Гратчастку рівнів вона не виконує жодного разу, тож
// зелений результат тут не є доказом `ФВ-6.13`.
//
// ⚠ Самі перевірки ЛИШАЮТЬСЯ і корисні: вони доводять, що обробник
// ПИТАЄ службу рішень і шанує відмову (не ставить задачу, не віддає
// зріз). Змінилася НЕ поведінка, а ЗАЯВКА про те, що вони покривають.
// Саму `ФВ-6.13` доводять `Ecr.Domain.Tests/Security/ResourceGrantTests` (гратчастка
// рівнів без жодного мока) і `Ecr.Api.Tests/CellWriteRoundTripTests` (через HTTP на
// живій базі).
public sealed class ExportAccessTests
{
    private const long DocumentId = 700;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ExportAccessTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.Export").Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_експорт_не_ставиться_в_чергу()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        // ⛔ B-08: невидимий документ — 404, як неіснуючий (`DocumentVisibility`), а не 403.
        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(DocumentId, Options(), CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", denied.ErrorCode);

        // ⚠ І задача НЕ поставлена. Відмова, після якої робота однаково
        // йде у фон, — це відмова лише на вигляд: файл усе одно з'явиться в
        // сховищі, а посилання на нього передбачуване.
        await _jobs.DidNotReceiveWithAnyArgs()
            .EnqueueAsync<IExcelExportJob>(null, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task З_грантом_експорт_ставиться_в_чергу()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(ReadScopes.Everything(new Domain.Entities.Configuration.TemplateVersionSnapshot(
                1, 0, [], new Dictionary<int, Domain.Entities.Configuration.ColumnDef>(),
                new Dictionary<(int, string), Domain.Entities.Configuration.RowDef>())));

        _jobs.EnqueueAsync<IExcelExportJob>(Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");

        // ⛔ S6: межі читання в завданні рахує СЕРВЕР — те, що передав викликач,
        // перезаписується.
        var jobId = await Handler().HandleAsync(
            DocumentId, Options() with { HiddenTableDefIds = [999], HiddenColumnDefIds = [998] }, CancellationToken.None);

        Assert.Equal("job-1", jobId);
        await _jobs.Received(1).EnqueueAsync<IExcelExportJob>(
            Arg.Is<object?>(p => ServerScoped(p)),
            Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    // ✎ AN-137 (S18, прохід C): експорт періоду ПОЗА областю читача давав 202 і порожню книгу, а зріз таблиці
    // того ж випадку - 404 `ECR-DOC-0404`. Тепер експорт відмовляє тією самою відповіддю (розкриття існування немає).
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Період_поза_областю_читача_як_неіснуючий_документ_і_задача_не_ставиться()
    {
        Allow(NarrowedTo(periodFrom: 202603, periodTo: 202603));

        var denied = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(DocumentId, Options(), CancellationToken.None)); // період 202601

        // Та сама відповідь, що й для невидимого документа та прихованої таблиці у зрізі.
        var invisible = DocumentVisibility.DocumentNotFound(DocumentId);
        Assert.Equal(invisible.ErrorCode, denied.ErrorCode);
        Assert.Equal(invisible.Message, denied.Message);
        Assert.Equal(invisible.Details!["messageKey"], denied.Details!["messageKey"]);

        await _jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<IExcelExportJob>(null, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Період_в_області_читача_експорт_ставиться_як_раніше()
    {
        Allow(NarrowedTo(periodFrom: 202601, periodTo: 202601));
        _jobs.EnqueueAsync<IExcelExportJob>(Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-2");

        var jobId = await Handler().HandleAsync(DocumentId, Options(), CancellationToken.None);

        Assert.Equal("job-2", jobId);
    }

    private void Allow(AccessProfile reader)
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(reader);
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(DocumentReadScope.For(reader, AccessBuilder.ProjectId, OneTableSnapshot()));
    }

    /// <summary>Читач з роллю, звуженою одним діапазоном періодів (D-214): грант на проєкт діє лише там.</summary>
    private static AccessProfile NarrowedTo(int periodFrom, int periodTo)
    {
        var key = $"{ResourceKind.Project}:{AccessBuilder.ProjectId}";
        var layer = new NarrowedAccess(
            null, new Ecr.Domain.ValueObjects.PeriodKey(periodFrom), new Ecr.Domain.ValueObjects.PeriodKey(periodTo),
            new Dictionary<string, GrantLevel> { [key] = GrantLevel.Read }, new HashSet<string>(),
            new HashSet<int> { 5 }, new HashSet<string>());
        var baseProfile = new AccessBuilder { UserId = 9 }.Permission("Document.Export").Build();

        return new AccessProfile
        {
            CacheKey = baseProfile.CacheKey,
            UserId = 9,
            SecurityStamp = baseProfile.SecurityStamp,
            Permissions = baseProfile.Permissions,
            Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int> { 5 },
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [AccessBuilder.ProjectId] = new(
                    new Dictionary<string, GrantLevel>(), new HashSet<string>(), new HashSet<int>(), new HashSet<string>())
                {
                    Narrowed = [layer],
                },
            },
        };
    }

    private static Domain.Entities.Configuration.TemplateVersionSnapshot OneTableSnapshot()
    {
        var text = new Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "x" });
        var sheet = new Domain.Entities.Configuration.SheetDef(1, Domain.ValueObjects.EcrCode.Create("SA"), text, 101);
        SetId(sheet, 101);
        var table = new Domain.Entities.Configuration.TableDef(
            101, Domain.ValueObjects.EcrCode.Create("T201"), text, 201,
            Domain.Enums.TableLayoutKind.PerPeriodInstance, Domain.Enums.TableRowMode.Fixed);
        SetId(table, 201);
        var column = new Domain.Entities.Configuration.ColumnDef(
            201, Domain.ValueObjects.EcrCode.Create("C301"), text, 301, Domain.Enums.CellDataType.Decimal);
        SetId(column, 301);
        table.AddColumn(column);
        sheet.AddTable(table);

        return new Domain.Entities.Configuration.TemplateVersionSnapshot(
            1, 0, [sheet], new Dictionary<int, Domain.Entities.Configuration.ColumnDef> { [301] = column },
            new Dictionary<(int, string), Domain.Entities.Configuration.RowDef>());
    }

    private static void SetId<T>(T e, int id) where T : Ecr.Domain.Abstractions.Entity<int>
        => typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(e, id);

    private static bool ServerScoped(object? payload)
        => payload is ExcelExportTask { Options: { HiddenTableDefIds: { Count: 0 }, HiddenColumnDefIds: { Count: 0 } } };

    private ExportDocumentHandler Handler()
        => new(_jobs, _access, _user, Substitute.For<IAuditWriter>(), Substitute.For<Ecr.Domain.Abstractions.IClock>());

    private static ExcelExportOptions Options()
        => new(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: 202601);
}
