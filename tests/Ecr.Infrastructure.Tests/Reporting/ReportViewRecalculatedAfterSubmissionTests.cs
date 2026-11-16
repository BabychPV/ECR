// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewRecalculatedAfterSubmissionTests.cs
using System.Globalization;
using System.Text.RegularExpressions;
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
/// B138 (рішення людини 16.11 «додай позначку у в'юхи»): вʼюхи сирих даних віддають позначку «перераховано після подання»
/// останньою службовою колонкою <c>_RecalculatedAfterSubmission</c> (tinyint, після <c>_WorkflowMode</c>): 1 — аркуш рядка
/// (аркуш × копія × період документа) ПОДАНИЙ і в журналі є запис <c>SheetRecalc / RecalculatedAfterSubmission</c> цього
/// документа, аркуша, копії й періоду, пізніший за <c>SubmittedAt</c>; інакше 0.
/// ⛔ Правило ОДНЕ: кожен випадок звіряє значення трьох видів вʼюх (версійна, стабільна, <c>rpt.v_DocumentCells</c>) з
/// карткою документа — <c>DocumentStore.FindAsync(...).Sheets[].RecalculatedAfterSubmission</c> — на ТИХ САМИХ даних.
/// Записи журналу кладуться константами й ключем <c>RecalculationSkips</c> (формат зміниться — тест червоний); справжній
/// записувач (<c>RecalculationJob</c>) звіряється з вʼюхою в <c>SheetNameRecalculationTests</c>.
/// Власна база, періоди 2099xx.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "RPT-STABLE")]
public sealed partial class ReportViewRecalculatedAfterSubmissionTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int UserId = 1;
    private const string Marked = RecalculationSkips.AuditRecalculatedAfterSubmission;

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
    public async Task Версійна_вʼюха_з_250_колонками_створюється_і_читається_з_позначкою()
    {
        string[] codes = [.. Enumerable.Range(1, 250).Select(i => $"C{i}")];
        var s = await ArrangeAsync(codes);
        await StateAsync(s.DocumentId, s, 209901, null, a => a.Submit(UserId, Now));
        await AuditAsync(s, s.DocumentId, 209901, null, Now.AddMinutes(1), Marked);

        Assert.Equal(250 + 13, (await ColumnNamesAsync(s.Versioned)).Count);   // 9 службових + 250 + 4 (копії, режим, позначка)
        Assert.Equal([(byte)1], await MarksAsync(
            $"SELECT DISTINCT _RecalculatedAfterSubmission FROM rpt.[{s.Versioned}] WHERE _DocumentId = @d AND _PeriodKey = 209901 AND [C1] = N'v'",
            s.DocumentId));
    }

    /// <summary>
    /// Ціна позначки на обсязі: 40 документів × 50 рядків, усі аркуші подані, половина позначена, плюс 5 000 сторонніх записів
    /// перерахунку того самого аркуша, пізніших за подання. Логічні читання повного SELECT стабільної вʼюхи: без колонки
    /// (той самий текст без приєднання й колонки), з колонкою (функція, приєднана за станом) і варіант A (підзапит на рядок).
    /// </summary>
    /// <remarks>
    /// ⚠ Межа попередня — до виміру на слоті (числа друкуються у вивід тесту): позначка не дорожча за варіант A і не більша
    /// за читання журналу «кандидати × стани» з запасом. Якщо вимір покаже інше — це рішення про індекс журналу, а не про тест.
    /// </remarks>
    [Fact]
    public async Task Позначка_на_обсязі_рахується_на_стан_аркуша_а_не_на_рядок()
    {
        const int documents = 40, rows = 50, foreign = 5000;
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

            OUTER APPLY (SELECT TOP (1) 1 AS ApprovalStateId
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
        Assert.True(shipped - without <= (long)documents * foreign * 4,
            $"позначка коштує {shipped - without} читань понад {without} — більше за «стани × кандидати журналу» з запасом");
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
            foreach (var p in new[] { period, second ?? 0 }.Where(p => p != 0))
            {
                var key = new PeriodKey(p);
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

    private Task SeedAsync(long documentId, int periodKey, Scenario s, long? copyId, string rowKey)
        => SeedRowsAsync(documentId, periodKey, s, 1, copyId, rowKey);

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
    private Task AuditAsync(Scenario s, long documentId, int period, long? copyId, DateTime at, string operation)
        => ExecAsync(string.Create(CultureInfo.InvariantCulture, $"""
            INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, ChangedByUserId, CorrelationId)
            VALUES ('{at:yyyy-MM-ddTHH:mm:ss.fff}', {s.VersionId}, N'{RecalculationSkips.AuditEntityType}', {s.SheetId}, 0, N'{operation}', {UserId},
                    N'{RecalculationSkips.AuditCorrelation(documentId, s.SheetId, period, copyId)}');
            """));

    /// <summary>
    /// Один скоуп (документ × аркуш × копія × період): картка документа і три види вʼюх дають те саме значення, і воно —
    /// очікуване. У вʼюхах — рівно одне значення на всі рядки скоупу.
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
            ("версійна", $"SELECT DISTINCT _RecalculatedAfterSubmission FROM rpt.[{s.Versioned}] {wide}"),
            ("стабільна", $"SELECT DISTINCT _RecalculatedAfterSubmission FROM rpt.[{s.Stable}] {wide}"),
            ("довга", "SELECT DISTINCT _RecalculatedAfterSubmission FROM rpt.v_DocumentCells "
                      + $"WHERE DocumentId = @d AND PeriodKey = {period} AND SheetCopyOrdinal = {ordinal}"),
        };
        foreach (var (kind, text) in views)
        {
            var marks = await MarksAsync(text, documentId);
            Assert.True(marks.Count == 1, $"{kind} вʼюха: {marks.Count} різних значень позначки для скоупу {documentId}/{period}/{copyId}");
            Assert.True(card == (marks[0] == 1),
                $"{kind} вʼюха дає {marks[0]}, картка документа — {card} (скоуп {documentId}/{period}/{copyId})");
        }

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

    [GeneratedRegex(@",\r?\n\s+CAST\(CASE WHEN ras\.ApprovalStateId IS NULL THEN 0 ELSE 1 END AS tinyint\) AS _RecalculatedAfterSubmission")]
    private static partial Regex MarkColumn();
}
