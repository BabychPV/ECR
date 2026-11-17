// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewRecalculatedAfterSubmissionTests.cs
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// B138 / B146 (D-536; рішення людини 16.11 «додай позначку у в'юхи»): вʼюхи сирих даних віддають позначку «перераховано після подання»
/// останньою службовою колонкою <c>_RecalculatedAfterSubmission</c> (tinyint, після <c>_WorkflowMode</c>): 1 — аркуш рядка
/// (аркуш × копія × період документа) ПОДАНИЙ і в журналі є запис <c>SheetRecalc / RecalculatedAfterSubmission</c> цього
/// документа, аркуша, копії й періоду, пізніший за <c>SubmittedAt</c>; інакше 0.
/// ⛔ Правило ОДНЕ: кожен випадок звіряє значення трьох видів вʼюх (версійна, стабільна, <c>rpt.v_DocumentCells</c>) з
/// карткою документа — <c>DocumentStore.FindAsync(...).Sheets[].RecalculatedAfterSubmission</c> — на ТИХ САМИХ даних.
/// Записи журналу кладуться константами й ключем <c>RecalculationSkips</c> (формат зміниться — тест червоний); справжній
/// записувач (<c>RecalculationJob</c>) звіряється з вʼюхою в <c>SheetNameRecalculationTests</c>.
/// Власна база, періоди 2099xx.
/// </summary>
/// <remarks>
/// Мутації (правила 401 і 410; локально 16.11, вихідний SQL + перезбірка, SQL у bin звірено з мутованим; пакет — мутації, що
/// червонять РІЗНІ тести; 13 — без двох тестів обсягу, <c>B138=Volume</c>). Повідомлення падіння називає види вʼюх
/// (<c>KINDS versioned/stable/long</c>), тож мутація одного виду видна окремо:
/// <list type="table">
/// <listheader><term>пакет: мутації</term><description>збірка; червоні (цей клас)</description></listheader>
/// <item><term>G1: <c>&gt;</c> → <c>&gt;=</c>; без умови статусу; <c>#</c> → <c>-</c> у ключі копії; період фіксовано 209901;
/// документ — шаблон <c>%</c>; без умови операції; без умови EntityType</term><description>OK; 8/13 — «same-moment»,
/// затвердження після позначки, копія (markCopy: True), інший період ×2, інший документ, «other-operation»,
/// «other-entity-type»</description></item>
/// <item><term>G2: без порівняння з <c>SubmittedAt</c>; ключ без частини копії</term><description>OK; 5/13 — «earlier»,
/// «same-moment», повторне подання, копія ×2</description></item>
/// <item><term>G3: <c>rc.EntityId = a.PeriodKey</c></term><description>OK; 7/13 — кожне очікування «позначено»</description></item>
/// <item><term>W1: константа — версійна 0, стабільна 1, довга (гаряча) 0</term><description>OK; 13/13, повідомлення називають
/// саме ці види</description></item>
/// <item><term>W2: константа — версійна 1, стабільна 0, довга (гаряча) 1</term><description>OK; 13/13</description></item>
/// <item><term>W3: приєднання <c>ras.ApprovalStateId IS NOT NULL</c> замість ключа стану (усі три види)</term>
/// <description>OK; 5/13 — інший документ, копія ×2, інший період ×2, усі три види</description></item>
/// <item><term>A2 / A3: архівна гілка довгої — константа 0 / приєднання не за станом</term><description>н/з (вихідний текст
/// читають Architecture-тести); ReportArchiveBranchTests 1/5 кожна</description></item>
/// <item><term>A4: правило за <c>Status = 2</c>; A5: статус порівнюється у вʼюсі</term><description>н/з; StatusNumberingTests
/// 1/5; A5 — 2/5 (StatusNumberingTests і ReportArchiveBranchTests)</description></item>
/// </list>
/// Позиція колонки (перед <c>_WorkflowMode</c>) — пакет B3 у <see cref="ReportViewWorkflowModeTests"/>.
/// AU F13 (17.11): скоуп сценарію — 2 рядки, перевіряється КОЖЕН рядок. Мутація AU1: гаряча гілка <c>rpt.v_DocumentCells</c>
/// позначає лише рядки з парним Id (<c>ISNULL(CASE WHEN r.Id % 2 = 0 THEN ras.Marked END, 0)</c>) — збірка OK; червоні 7 із 16
/// цього класу, кожен — «KINDS long = [0,1]» (усі випадки «позначено»); на скоупі з одного рядка вона лишалась зеленою.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "RPT-STABLE")]
public sealed partial class ReportViewRecalculatedAfterSubmissionTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int UserId = 1;
    private const string Marked = RecalculationSkips.AuditRecalculatedAfterSubmission;

    /// <summary>Рядків у кожному скоупі сценарію (≥ 2: перевірка «кожен рядок скоупу несе позначку картки»).</summary>
    private const int ScopeRows = 2;

    private static readonly DateTime Now = new(2099, 1, 2, 10, 0, 0, DateTimeKind.Utc);

    private readonly SqlServerFixture _sql = SqlServerFixture.WithOwnDatabase("_b138r");

    public Task InitializeAsync() => _sql.InitializeAsync();

    public Task DisposeAsync() => _sql.DisposeAsync();

    [Fact]
    public async Task Поданий_аркуш_перерахований_після_подання_позначено_і_лише_його()
    {
        var s = await ArrangeAsync();
        var other = await NewDocumentAsync(s, 209901);   // інший документ того самого аркуша й періоду
        await StateAsync(s.DocumentId, s, 209901, null, a => a.Submit(UserId, Now));
        await StateAsync(other, s, 209901, null, a => a.Submit(UserId, Now));
        await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(1), Marked);

        await AssertScopeAsync(s, s.DocumentId, 209901, null, expected: true);
        await AssertScopeAsync(s, other, 209901, null, expected: false);
    }

    /// <summary>
    /// Не позначено: без запису журналу; запис раніший за подання; запис рівно в момент подання (порівняння суворе);
    /// запис іншої операції (<c>SkippedApproved</c>) з тим самим ключем; аркуш без стану.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("earlier")]
    [InlineData("same-moment")]
    [InlineData("other-operation")]
    [InlineData("other-entity-type")]
    [InlineData("no-state")]
    public async Task Без_запису_пізнішого_за_подання_позначки_немає(string what)
    {
        var s = await ArrangeAsync();
        if (what != "no-state")
        {
            await StateAsync(s.DocumentId, s, 209901, null, a => a.Submit(UserId, Now));
        }

        switch (what)
        {
            case "earlier": await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(-1), Marked); break;
            case "same-moment": await AuditAsync(s, s.DocumentId, 209901, null, Now, Marked); break;
            case "other-operation": await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(1), RecalculationSkips.AuditSkippedApproved); break;
            case "other-entity-type": await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(1), Marked, "OtherEntity"); break;
            case "no-state": await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(1), Marked); break;
        }

        await AssertScopeAsync(s, s.DocumentId, 209901, null, expected: false);
    }

    [Fact]
    public async Task Повторне_подання_після_позначки_знімає_її()
    {
        var s = await ArrangeAsync();
        await StateAsync(s.DocumentId, s, 209901, null, a => a.Submit(UserId, Now));
        await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(1), Marked);
        await AssertScopeAsync(s, s.DocumentId, 209901, null, expected: true);

        await UpdateStateAsync(s.DocumentId, s, 209901, null, a =>
        {
            a.Reject(UserId, "fix", Now.AddMinutes(2));
            a.Submit(UserId, Now.AddMinutes(3));
        });

        await AssertScopeAsync(s, s.DocumentId, 209901, null, expected: false);
    }

    [Fact]
    public async Task Затвердження_після_позначки_знімає_її()
    {
        var s = await ArrangeAsync();
        await StateAsync(s.DocumentId, s, 209901, null, a => a.Submit(UserId, Now));
        await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(1), Marked);
        await AssertScopeAsync(s, s.DocumentId, 209901, null, expected: true);

        await UpdateStateAsync(s.DocumentId, s, 209901, null, a => a.Approve(UserId, Now.AddMinutes(2)));

        await AssertScopeAsync(s, s.DocumentId, 209901, null, expected: false);
    }

    /// <summary>Копія аркуша має власний стан і власний ключ журналу («…:{аркуш}#{копія}:{період}»): позначка — лише свого.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Копія_аркуша_позначається_за_власним_станом_і_ключем(bool markCopy)
    {
        var s = await ArrangeAsync();
        await StateAsync(s.DocumentId, s, 209901, null, a => a.Submit(UserId, Now));
        await StateAsync(s.DocumentId, s, 209901, s.CopyId, a => a.Submit(UserId, Now));
        await AuditAsync(s, s.DocumentId, 209901, markCopy ? s.CopyId : null, Now.AddMinutes(1), Marked);

        await AssertScopeAsync(s, s.DocumentId, 209901, s.CopyId, expected: markCopy);
        await AssertScopeAsync(s, s.DocumentId, 209901, null, expected: !markCopy);
    }

    [Theory]
    [InlineData(209901)]
    [InlineData(209902)]
    public async Task Інший_період_того_самого_документа_має_власну_позначку(int markedPeriod)
    {
        var s = await ArrangeAsync();
        await StateAsync(s.DocumentId, s, 209901, null, a => a.Submit(UserId, Now));
        await StateAsync(s.DocumentId, s, 209902, null, a => a.Submit(UserId, Now));
        await AuditAsync(s, s.DocumentId, markedPeriod, null, Now.AddMinutes(1), Marked);

        await AssertScopeAsync(s, s.DocumentId, 209901, null, expected: markedPeriod == 209901);
        await AssertScopeAsync(s, s.DocumentId, 209902, null, expected: markedPeriod == 209902);
    }

    /// <summary>
    /// Межа кількості таблиць у запиті: версійна вʼюха таблиці з 250 колонками (по приєднанню на колонку) з двома новими
    /// посиланнями (стан аркуша у функції та журнал) створюється й читається.
    /// </summary>
    [Fact]
    [Trait("B138", "Volume")]
    public async Task Версійна_вʼюха_з_250_колонками_створюється_і_читається_з_позначкою()
    {
        string[] codes = [.. Enumerable.Range(1, 250).Select(i => $"C{i}")];
        var s = await ArrangeAsync(codes);
        await StateAsync(s.DocumentId, s, 209901, null, a => a.Submit(UserId, Now));
        await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(1), Marked);

        Assert.Equal(250 + 13, (await ColumnNamesAsync(s.Versioned)).Count);   // 9 службових + 250 + 4 (копії, режим, позначка)
        // Оригінал подано й позначено; копія аркуша за той самий період має власний стан (його немає) — 0.
        var read = $"SELECT DISTINCT _RecalculatedAfterSubmission FROM rpt.[{s.Versioned}] WHERE _DocumentId = @d AND _PeriodKey = 209901 AND [C1] = N'v'";
        Assert.Equal([(byte)1], await MarksAsync(read + " AND _SheetCopyOrdinal = 1", s.DocumentId));
        Assert.Equal([(byte)0], await MarksAsync(read + " AND _SheetCopyOrdinal = 2", s.DocumentId));
    }

    /// <summary>
    /// Ціна позначки на обсязі: 40 документів × 50 рядків, усі аркуші подані, половина позначена, плюс 5 000 сторонніх записів
    /// перерахунку того самого аркуша, пізніших за подання. Логічні читання повного SELECT стабільної вʼюхи: без колонки
    /// (той самий текст без приєднання й колонки), з колонкою (функція, приєднана за станом) і варіант A (підзапит на рядок).
    /// </summary>
    /// <remarks>
    /// Виміри 16.11 без індексу B146: без колонки 8 074; з колонкою 10 577 (5 000 записів) і 17 797 (20 000) — ціна росла з
    /// журналом (~0,48 читання на запис). З IX_StructureChange_Recalc і FORCESEEK (D-536) межа — «12 читань на стан + 200»
    /// для обох точок, і план — seek на цьому індексі без жодного скану aud.StructureChange. Числа й план друкуються у вивід.
    /// Мутація: без індексу в 11-audit-tables.sql — FORCESEEK на відсутній індекс, вʼюха не читається (обидві точки червоні).
    /// </remarks>
    [Theory]
    [Trait("B138", "Volume")]
    [InlineData(5000)]
    [InlineData(20000)]
    public async Task Позначка_на_обсязі_рахується_на_стан_аркуша_а_не_на_рядок(int foreign)
    {
        const int documents = 40, rows = 50;
        var s = await ArrangeAsync();
        var ids = new List<long>();   // документ сценарію лишається без стану (його рядки — 0)
        for (var i = 0; i < documents; i++)
        {
            ids.Add(await NewDocumentAsync(s, 209901, seed: false));
        }

        for (var i = 0; i < ids.Count; i++)
        {
            await SeedRowsAsync(ids[i], 209901, s, rows);
            await StateAsync(ids[i], s, 209901, null, a => a.Submit(UserId, Now));
            if (i % 2 == 0)
            {
                await AuditAsync(s, ids[i], 209901, null, Now.AddMinutes(1), Marked);
            }
        }

        // Сторонні записи того самого аркуша (EntityId), пізніші за подання: документи, яких у вʼюсі немає.
        await ExecAsync($"""
            INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangedByUserId, CorrelationId)
            SELECT DATEADD(SECOND, CAST(n.N AS int), '2099-01-02T11:00:00'), {s.VersionId}, N'{RecalculationSkips.AuditEntityType}', {s.SheetId}, 0,
                   CASE WHEN n.N % 2 = 0 THEN N'{Marked}' ELSE N'{RecalculationSkips.AuditSkippedApproved}' END, {UserId},
                   CONCAT(N'sheet-recalc:', 900000000 + n.N, N':', {s.SheetId}, N':209901')
              FROM (SELECT TOP ({foreign}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
                      FROM sys.all_objects AS x CROSS JOIN sys.all_objects AS y) AS n;
            """);

        var text = await DefinitionAsync(s.Stable);
        var baseView = s.Stable + "_base";
        var applyView = s.Stable + "_varA";
        var baseText = MarkColumn().Replace(MarkJoin().Replace(text, ""), "");
        var applyText = MarkJoin().Replace(text, """

            OUTER APPLY (SELECT TOP (1) CAST(1 AS tinyint) AS Marked
                           FROM aud.StructureChange AS rc
                          WHERE a.Status = 1
                            AND rc.EntityType = N'SheetRecalc' AND rc.EntityId = a.SheetDefId
                            AND rc.ChangedAt > a.SubmittedAt
                            AND rc.Operation = N'RecalculatedAfterSubmission'
                            AND rc.CorrelationId = CONCAT(N'sheet-recalc:', CAST(a.DocumentId AS nvarchar(20)), N':',
                                    CAST(a.SheetDefId AS nvarchar(12)),
                                    CASE WHEN a.SheetCopyId IS NULL THEN N'' ELSE CONCAT(N'#', CAST(a.SheetCopyId AS nvarchar(20))) END,
                                    N':', CAST(a.PeriodKey AS nvarchar(10)))) AS ras
            """.ReplaceLineEndings("\n"));
        Assert.NotEqual(text, baseText);
        Assert.NotEqual(text, applyText);
        await ExecAsync(CreateAs(baseText, baseView));
        await ExecAsync(CreateAs(applyText, applyView));

        var marked = await CountAsync($"SELECT COUNT(*) FROM rpt.[{s.Stable}] WHERE _RecalculatedAfterSubmission = 1");
        Assert.Equal(marked, await CountAsync($"SELECT COUNT(*) FROM rpt.[{applyView}] WHERE _RecalculatedAfterSubmission = 1"));
        Assert.Equal(documents / 2 * rows, marked);

        var without = await ReadsAsync(baseView);
        var shipped = await ReadsAsync(s.Stable);
        var perRow = await ReadsAsync(applyView);
        output.WriteLine($"logical reads, {documents} documents x {rows} rows, {foreign} foreign audit rows: "
                         + $"without column {without}; with column (function joined on state) {shipped}; variant A (apply per row) {perRow}");

        Assert.True(shipped <= perRow * 11 / 10, $"позначка за станом ({shipped}) дорожча за підзапит на рядок ({perRow})");

        // B146 (D-536): з IX_StructureChange_Recalc і FORCESEEK - seek на стан аркуша; ціна обмежена кількістю станів
        // (тут documents), а не обсягом журналу: однакова межа для 5 000 і 20 000 сторонніх записів.
        Assert.True(shipped - without <= (12L * documents) + 200,
            $"позначка коштує {shipped - without} читань понад {without} — більше за «12 на стан + 200» ({documents} станів, {foreign} сторонніх записів)");
        var plan = await PlanAsync($"SELECT COUNT_BIG(*), MAX(CHECKSUM(*)) FROM rpt.[{s.Stable}]");
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var journal = System.Xml.Linq.XDocument.Parse(plan).Descendants(ns + "RelOp")
            .Where(r => r.Elements().Elements(ns + "Object").Any(o => (string?)o.Attribute("Table") == "[StructureChange]"))
            .Select(r => $"{(string?)r.Attribute("PhysicalOp")}:{r.Elements().Elements(ns + "Object").First().Attribute("Index")?.Value}"
                         + ":seek=" + string.Join(",", r.Descendants(ns + "SeekPredicates").Descendants(ns + "ColumnReference")
                             .Select(c => (string?)c.Attribute("Column")).Where(c => c is not null && c.Length > 0 && !c.StartsWith("Expr", StringComparison.Ordinal))
                             .Distinct()))
            .ToList();
        output.WriteLine($"plan on aud.StructureChange: {string.Join("; ", journal)}");
        // Seek саме за ключем стану (CorrelationId у ключі пошуку), а не діапазон усіх записів операції з hash-зʼєднанням.
        Assert.Contains(journal, op => op.StartsWith("Index Seek:[IX_StructureChange_Recalc]", StringComparison.Ordinal)
                                       && op.Contains("CorrelationId", StringComparison.Ordinal));
        Assert.DoesNotContain(journal, op => op.Contains("Scan", StringComparison.Ordinal));
    }

    /// <summary>Фактичний план запиту (SET STATISTICS XML ON) - перший план, що читає aud.StructureChange.</summary>
    private async Task<string> PlanAsync(string text)
    {
        await using var connection = await OpenAsync();
        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET STATISTICS XML ON;";
            await on.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = text;
#pragma warning restore CA2100
        command.CommandTimeout = 600;
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase) && await reader.ReadAsync())
            {
                var xml = reader.GetString(0);
                if (xml.Contains("StructureChange", StringComparison.Ordinal))
                {
                    return xml;
                }
            }
        }
        while (await reader.NextResultAsync());

        throw new InvalidOperationException("SQL Server не повернув план, що читає aud.StructureChange.");
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Scenario(
        int ProjectVersionId, int VersionId, int SheetId, int TableId, int FirstColumnId, long DocumentId, long CopyId,
        string Versioned, string Stable);

    /// <summary>
    /// Шаблон з однією версією (аркуш S, таблиця T), документ із періодами 209901 і 209902: по рядку з коміркою в оригіналі
    /// кожного періоду та в копії аркуша за 209901; вʼюхи згенеровано.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(string[]? columns = null)
    {
        columns ??= ["A"];
        string code;
        int versionId, sheetId, tableId, columnId;
        await using (var db = _sql.CreateContext())
        {
            code = "RA" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var template = new Template(EcrCode.Create(code), Name(code), 1, Now);
            db.Templates.Add(template);
            await db.SaveChangesAsync();
            var version = new TemplateVersion(template.Id, "1", 1, Now);
            db.TemplateVersions.Add(version);
            await db.SaveChangesAsync();
            var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync();
            var table = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync();
            var defs = columns.Select((c, i) => new ColumnDef(table.Id, EcrCode.Create(c), Name(c), i + 1, CellDataType.String)).ToList();
            db.ColumnDefs.AddRange(defs);
            await db.SaveChangesAsync();
            version.Publish(1, Now);
            await db.SaveChangesAsync();
            (versionId, sheetId, tableId, columnId) = (version.Id, sheet.Id, table.Id, defs[0].Id);
        }

        var draft = new Scenario(versionId, versionId, sheetId, tableId, columnId, 0, 0,
            Sanitise($"v_{code}_S_T_v1"), Sanitise($"v_{code}_S_T"));
        var documentId = await NewDocumentAsync(draft, 209901, 209902);
        long copyId;
        await using (var db = _sql.CreateContext())
        {
            var original = new DocumentSheet(documentId, sheetId);
            db.DocumentSheets.Add(original);
            await db.SaveChangesAsync();
            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync();
            copyId = copy.Id;
        }

        var s = draft with { DocumentId = documentId, CopyId = copyId };
        await SeedAsync(documentId, 209902, s, null, "R1");
        await SeedAsync(documentId, 209901, s, copyId, "R1");
        await ExecAsync($"EXEC rpt.usp_GenerateTemplateViews {versionId}, 0");
        await ExecAsync($"EXEC rpt.usp_GenerateStableViews {versionId}, 0, 0");
        return s;
    }

    /// <summary>Проєкт і документ версії сценарію; за замовчуванням — рядок з коміркою в оригіналі першого періоду.</summary>
    private async Task<long> NewDocumentAsync(Scenario s, int period, int? second = null, bool seed = true)
    {
        long documentId;
        await using (var db = _sql.CreateContext())
        {
            var tag = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
            var project = new Ecr.Domain.Entities.Documents.Project(
                EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31),
                s.ProjectVersionId, PeriodKind.Monthly, policyId, "Asia/Atyrau");
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            foreach (var value in new[] { period, second ?? 0 }.Where(k => k != 0))
            {
                var key = new PeriodKey(value);
                db.Periods.Add(new Period(project.Id, key, (byte)key.Sequence, new DateOnly(key.Year, key.Sequence, 1),
                    new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence))));
            }

            var document = new Document(project.Id, "DOC" + tag, 1, Now);
            db.Documents.Add(document);
            await db.SaveChangesAsync();
            documentId = document.Id;
        }

        if (seed)
        {
            await SeedAsync(documentId, period, s, null, "R1");
        }

        return documentId;
    }

    /// <summary>
    /// Скоуп сценарію (аркуш × копія × період документа) — <see cref="ScopeRows"/> рядків, а не один: позначка рахується на
    /// стан, і перевірка має бачити, що її несе КОЖЕН рядок скоупу (AU F13: мутація «позначено лише кожен другий рядок»
    /// на скоупі з одного рядка лишалась зеленою).
    /// </summary>
    private Task SeedAsync(long documentId, int periodKey, Scenario s, long? copyId, string rowKey)
        => SeedRowsAsync(documentId, periodKey, s, ScopeRows, copyId, rowKey);

    /// <summary>Рядки з коміркою першої колонки («v») в екземплярі таблиці (оригінал або копія) за період.</summary>
    private async Task SeedRowsAsync(long documentId, int periodKey, Scenario s, int count, long? copyId = null, string? firstRowKey = null)
    {
        var loader = new BulkCellLoader(_sql.ConnectionString, 1000);
        var key = new PeriodKey(periodKey);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", count, CancellationToken.None);
        await using (var db = _sql.CreateContext())
        {
            db.TableInstances.Add(new TableInstance(key, instanceId, documentId, s.TableId, Now, copyId));
            await db.SaveChangesAsync();
            db.TableRows.AddRange(Enumerable.Range(0, count).Select(i => new TableRow(
                key, firstRowId + i, instanceId, RowKey.Create(i == 0 && firstRowKey is not null ? firstRowKey : $"R{i + 1}"), i + 2, Now)));
            await db.SaveChangesAsync();
        }

        await ExecAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString) "
            + $"SELECT {periodKey}, r.Id, {s.FirstColumnId}, {s.TableId}, N'v' FROM doc.TableRow AS r "
            + $"WHERE r.PeriodKey = {periodKey} AND r.TableInstanceId = {instanceId};");
    }

    private async Task StateAsync(long documentId, Scenario s, int period, long? copyId, Action<ApprovalState> act)
    {
        await using var db = _sql.CreateContext();
        var state = new ApprovalState(documentId, s.SheetId, period, copyId);
        act(state);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    private async Task UpdateStateAsync(long documentId, Scenario s, int period, long? copyId, Action<ApprovalState> act)
    {
        await using var db = _sql.CreateContext();
        var state = await db.ApprovalStates.SingleAsync(
            a => a.DocumentId == documentId && a.SheetDefId == s.SheetId && a.PeriodKey == period && a.SheetCopyId == copyId);
        act(state);
        await db.SaveChangesAsync();
    }

    /// <summary>Запис журналу аркуша перерахунку — як його пише <c>RecalculationJob</c> (EntityId = аркуш, ключ <c>AuditCorrelation</c>).</summary>
    private Task AuditAsync(
        Scenario s, long documentId, int period, long? copyId, DateTime at, string operation, string entityType = RecalculationSkips.AuditEntityType)
        => ExecAsync(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangedByUserId, CorrelationId)
            VALUES ('{at:yyyy-MM-ddTHH:mm:ss.fff}', {s.VersionId}, N'{entityType}', {s.SheetId}, 0, N'{operation}', {UserId},
                    N'{RecalculationSkips.AuditCorrelation(documentId, s.SheetId, period, copyId)}');
            """));

    /// <summary>
    /// Один скоуп (документ × аркуш × копія × період): картка документа і три види вʼюх дають те саме значення, і воно —
    /// очікуване. У КОЖНОМУ з <see cref="ScopeRows"/> рядків скоупу кожного виду вʼюх — те саме значення, що в картці.
    /// </summary>
    private async Task AssertScopeAsync(Scenario s, long documentId, int period, long? copyId, bool expected)
    {
        bool card;
        await using (var db = _sql.CreateContext())
        {
            var summary = await new DocumentStore(db).FindAsync(documentId, new PeriodKeyFilter(period), CancellationToken.None);
            Assert.NotNull(summary);
            card = (summary.Sheets ?? []).Any(x => x.Code == SheetCopyNaming.StateKey("S", copyId) && x.RecalculatedAfterSubmission);
        }

        var ordinal = copyId is null ? 1 : 2;
        var wide = $"WHERE _DocumentId = @d AND _PeriodKey = {period} AND _SheetCopyOrdinal = {ordinal}";
        var views = new (string Kind, string Text)[]
        {
            ("versioned", $"SELECT _RecalculatedAfterSubmission FROM rpt.[{s.Versioned}] {wide} ORDER BY _RowKey"),
            ("stable", $"SELECT _RecalculatedAfterSubmission FROM rpt.[{s.Stable}] {wide} ORDER BY _RowKey"),
            ("long", "SELECT _RecalculatedAfterSubmission FROM rpt.v_DocumentCells "
                      + $"WHERE DocumentId = @d AND PeriodKey = {period} AND SheetCopyOrdinal = {ordinal} ORDER BY RowKey"),
        };
        // Усі три види перевіряються до кінця, розбіжності — одним повідомленням з назвами видів (мутація одного виду вʼюх
        // називає саме його).
        var wrong = new List<string>();
        foreach (var (kind, text) in views)
        {
            var marks = await MarksAsync(text, documentId);
            // Рівно рядки скоупу (кожен рядок — одна комірка) і кожен із позначкою картки.
            if (marks.Count != ScopeRows || marks.Any(m => (m == 1) != card))
            {
                wrong.Add($"{kind} = [{string.Join(",", marks)}]");
            }
        }

        Assert.True(wrong.Count == 0,
            $"KINDS {string.Join("; ", wrong)} проти картки документа {card} (скоуп {documentId}/{period}/{copyId})");
        Assert.Equal(expected, card);
    }

    private async Task<List<byte>> MarksAsync(string text, long documentId)
    {
        var list = new List<byte>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // імена — з кодів шаблону тесту
        command.CommandText = text;
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@d", documentId);
        command.CommandTimeout = 300;   // перше читання широкої вʼюхи (250 колонок) компілює план; під навантаженням машини > 30 с
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetByte(0));
        }

        return list;
    }

    private async Task<List<string>> ColumnNamesAsync(string view)
    {
        var list = new List<string>();
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.name FROM sys.columns AS c WHERE c.object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V') ORDER BY c.column_id";
        command.Parameters.AddWithValue("@v", view);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private async Task<string> DefinitionAsync(string view)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(N'rpt.' + QUOTENAME(@v), N'V'))";
        command.Parameters.AddWithValue("@v", view);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Той самий текст вʼюхи під іншим ім'ям (порівняльна вʼюха виміру; лише у власній базі тесту).</summary>
    private static string CreateAs(string definition, string name)
        => ViewHead().Replace(definition, $"CREATE OR ALTER VIEW rpt.[{name}]", 1);

    /// <summary>Логічні читання повного SELECT вʼюхи (найменше з 3 прогонів після прогріву).</summary>
    private async Task<long> ReadsAsync(string view)
    {
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            long reads = 0;
            await using var connection = new SqlConnection(_sql.ConnectionString);
            connection.InfoMessage += (_, e) =>
            {
                foreach (var m in LogicalReads().Matches(e.Message).Cast<Match>())
                {
                    reads += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
            };
            await connection.OpenAsync();
            await using (var set = connection.CreateCommand())
            {
                set.CommandText = "SET STATISTICS IO ON";
                await set.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
#pragma warning disable CA2100
            command.CommandText = $"SELECT COUNT_BIG(*), MAX(CHECKSUM(*)) FROM rpt.[{view}]";
#pragma warning restore CA2100
            command.CommandTimeout = 600;
            await command.ExecuteScalarAsync();
            if (attempt > 0)
            {
                best = Math.Min(best, reads);
            }
        }

        return best;
    }

    private async Task<int> CountAsync(string text)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = text;
#pragma warning restore CA2100
        command.CommandTimeout = 600;
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string text)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = text;
#pragma warning restore CA2100
        command.CommandTimeout = 600;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.NextResultAsync())
        {
        }
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_sql.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Sanitise(string name) => NotIdentifier().Replace(name, "_");

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();

    [GeneratedRegex(@"logical reads (\d+)")]
    private static partial Regex LogicalReads();

    [GeneratedRegex(@"^\s*CREATE\s+(OR\s+ALTER\s+)?VIEW\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex ViewHead();

    [GeneratedRegex(@"\r?\nLEFT JOIN rpt\.fn_RecalculatedAfterSubmission\(\) AS ras ON ras\.ApprovalStateId = a\.Id")]
    private static partial Regex MarkJoin();

    [GeneratedRegex(@",\r?\n\s+ISNULL\(ras\.Marked, 0\) AS _RecalculatedAfterSubmission")]
    private static partial Regex MarkColumn();
}
