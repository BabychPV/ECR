using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Серверне сортування й курсорна пагінація журналів аудиту (комірки, структура, безпека, погодження):
/// типово найновіші першими, «за часом» в обох напрямках і «за користувачем» без дублів і пропусків
/// на межах сторінок, у тому числі за однакових моментів; курсор чужого порядку відхиляється.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ: у <c>AuditPaging.Order</c> прибрати завершальний <c>Id</c> — червоніють перевірки з
/// однаковими моментами; у <c>AuditPaging.Keyset</c> замінити <c>&lt;</c> на <c>&gt;</c> — червоніють усі обходи;
/// у <c>AuditCursor.Decode</c> прибрати перевірку порядку — червоніє <c>Курсор_чужого_порядку_відхиляється</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditSortPagingTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Рядок засіву: індекс запису (= порядок Id), момент, автор.</summary>
    private sealed record Seed(int Index, DateTime At, int UserId, string Name);

    // Моменти з повторами: межа сторінки падає всередину групи з однаковим ChangedAt за будь-якого розміру сторінки.
    private static readonly int[] Minutes = [0, 5, 5, 5, 10, 10, 20, 20, 20, 20, 30];

    private static List<Seed> Plan(IReadOnlyList<(int Id, string Name)> users)
        => [.. Minutes.Select((m, i) => new Seed(i, At.AddMinutes(m), users[i % users.Count].Id, users[i % users.Count].Name))];

    private static IEnumerable<int> Expected(IEnumerable<Seed> seeds, AuditSort sort)
        => sort switch
        {
            AuditSort.ChangedAtDesc => seeds.OrderByDescending(s => s.At).ThenByDescending(s => s.Index).Select(s => s.Index),
            AuditSort.ChangedAtAsc => seeds.OrderBy(s => s.At).ThenBy(s => s.Index).Select(s => s.Index),
            AuditSort.UserAsc => seeds.OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.UserId)
                .ThenBy(s => s.At).ThenBy(s => s.Index).Select(s => s.Index),
            _ => seeds.OrderByDescending(s => s.Name, StringComparer.Ordinal).ThenByDescending(s => s.UserId)
                .ThenByDescending(s => s.At).ThenByDescending(s => s.Index).Select(s => s.Index),
        };

    public static IEnumerable<object[]> Sorts()
    {
        foreach (var sort in Enum.GetValues<AuditSort>())
        {
            foreach (var limit in new[] { 1, 2, 3, 4 })
            {
                yield return [sort, limit];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Sorts))]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Журнал_комірок_обхід_без_дублів_і_пропусків_у_кожному_порядку(AuditSort sort, int limit)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var users = await UsersAsync();
        var seeds = Plan(users);

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);
        foreach (var s in seeds)
        {
            await writer.WriteCellChangesAsync([Cell(doc, s)], CancellationToken.None);
        }

        var reader = new AuditReader(db);
        var filter = new CellChangeFilter(At.AddMinutes(-1), At.AddHours(2), DocumentId: doc.DocumentId, Sort: sort);

        var walked = await WalkAsync(
            cursor => reader.ReadCellChangesAsync(filter, new CursorRequest(limit, cursor), CancellationToken.None),
            c => int.Parse(c.RowKey[1..], CultureInfo.InvariantCulture));

        Assert.Equal(Expected(seeds, sort), walked);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Типовий_порядок_журналу_комірок_найновіші_першими()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var seeds = Plan(await UsersAsync());

        await using var db = builder.CreateContext();
        foreach (var s in seeds)
        {
            await new AuditWriter(db).WriteCellChangesAsync([Cell(doc, s)], CancellationToken.None);
        }

        // Фільтр БЕЗ Sort — типове значення; перший рядок — найпізніший момент, а за рівного моменту — найбільший Id.
        var page = await new AuditReader(db).ReadCellChangesAsync(
            new CellChangeFilter(At.AddMinutes(-1), At.AddHours(2), DocumentId: doc.DocumentId),
            new CursorRequest(3), CancellationToken.None);

        Assert.Equal(seeds.Max(s => s.At), page.Items[0].ChangedAt);
        Assert.Equal("r10", page.Items[0].RowKey);
        Assert.True(page.Items.Zip(page.Items.Skip(1)).All(p => p.First.ChangedAt >= p.Second.ChangedAt));
    }

    [Theory]
    [MemberData(nameof(Sorts))]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Журнал_структури_обхід_без_дублів_і_пропусків_у_кожному_порядку(AuditSort sort, int limit)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var users = await UsersAsync();
        var seeds = Plan(users);
        var entityType = $"cfg.Sort-{Guid.NewGuid():N}"[..40];

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);
        foreach (var s in seeds)
        {
            await writer.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    s.At, doc.TemplateVersionId, entityType, s.Index + 1, ChangeClass.Safe, "SaveRules", null, "{}", null, s.UserId, null),
                CancellationToken.None);
        }

        var reader = new AuditReader(db);
        var filter = new StructureChangeFilter(At.AddMinutes(-1), At.AddHours(2), EntityType: entityType, Sort: sort);

        var walked = await WalkAsync(
            cursor => reader.ReadStructureJournalAsync(filter, new CursorRequest(limit, cursor), CancellationToken.None),
            c => c.EntityId - 1);

        Assert.Equal(Expected(seeds, sort), walked);
    }

    [Theory]
    [MemberData(nameof(Sorts))]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Журнал_безпеки_обхід_без_дублів_і_пропусків_у_кожному_порядку(AuditSort sort, int limit)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await builder.BuildAsync(ct: CancellationToken.None);
        var users = await UsersAsync();
        var seeds = Plan(users);
        var eventType = $"SortTest{Guid.NewGuid():N}"[..40];

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);
        foreach (var s in seeds)
        {
            await writer.WriteSecurityEventAsync(
                new SecurityEventRecord(s.At, eventType, s.Index + 1, null, null, s.UserId, null), CancellationToken.None);
        }

        var reader = new AuditReader(db);
        var filter = new SecurityEventFilter(At.AddMinutes(-1), At.AddHours(2), EventType: eventType, Sort: sort);

        var walked = await WalkAsync(
            cursor => reader.ReadSecurityEventsAsync(filter, new CursorRequest(limit, cursor), CancellationToken.None),
            e => e.TargetUserId!.Value - 1);

        Assert.Equal(Expected(seeds, sort), walked);
    }

    [Theory]
    [MemberData(nameof(Sorts))]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Журнал_погодження_обхід_без_дублів_і_пропусків_у_кожному_порядку(AuditSort sort, int limit)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var users = await UsersAsync();
        var seeds = Plan(users);

        // Рядок засіву кодується в `Reason` ("e<index>"): подія погодження власних ключів для цього не має.
        foreach (var s in seeds)
        {
            await InsertWorkflowEventAsync(doc, s);
        }

        await using var db = builder.CreateContext();
        var reader = new AuditReader(db);
        var filter = new WorkflowEventFilter(At.AddMinutes(-1), At.AddHours(2), DocumentId: doc.DocumentId, Sort: sort);

        var walked = await WalkAsync(
            cursor => reader.ReadWorkflowEventsAsync(filter, new CursorRequest(limit, cursor), CancellationToken.None),
            e => int.Parse(e.Comment![1..], CultureInfo.InvariantCulture));

        Assert.Equal(Expected(seeds, sort), walked);

        // Підрахунок для стелі експорту рахує рівно те, що віддає перелік.
        Assert.Equal(seeds.Count, await reader.CountWorkflowEventsAsync(filter, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Автор_якого_немає_в_порядку_за_користувачем_іде_першим_а_не_губиться()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var users = await UsersAsync();

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);
        await writer.WriteCellChangesAsync(
            [Cell(doc, new Seed(0, At, users[0].Id, users[0].Name)), Cell(doc, new Seed(1, At, 2_000_000_003, string.Empty))],
            CancellationToken.None);

        var page = await new AuditReader(db).ReadCellChangesAsync(
            new CellChangeFilter(At.AddMinutes(-1), At.AddHours(1), DocumentId: doc.DocumentId, Sort: AuditSort.UserAsc),
            new CursorRequest(50), CancellationToken.None);

        // Відсутній автор — порожнє ім'я — сортується першим у зростанні, його рядок не ховається.
        Assert.Equal(["r1", "r0"], page.Items.Select(c => c.RowKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Курсор_чужого_порядку_відхиляється_а_не_читається_мовчки()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var seeds = Plan(await UsersAsync());

        await using var db = builder.CreateContext();
        foreach (var s in seeds)
        {
            await new AuditWriter(db).WriteCellChangesAsync([Cell(doc, s)], CancellationToken.None);
        }

        var reader = new AuditReader(db);
        CellChangeFilter With(AuditSort sort) => new(At.AddMinutes(-1), At.AddHours(2), DocumentId: doc.DocumentId, Sort: sort);

        var page = await reader.ReadCellChangesAsync(With(AuditSort.ChangedAtDesc), new CursorRequest(2), CancellationToken.None);
        Assert.NotNull(page.NextCursor);

        foreach (var other in new[] { AuditSort.ChangedAtAsc, AuditSort.UserAsc, AuditSort.UserDesc })
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => reader.ReadCellChangesAsync(
                With(other), new CursorRequest(2, page.NextCursor), CancellationToken.None));

            Assert.Equal("err.ECR-REQ-0422.auditCursorSortMismatch", ex.Details!["messageKey"]);
        }

        // Той самий порядок — працює; сміття — як і раніше початок списку, не помилка.
        Assert.NotEmpty((await reader.ReadCellChangesAsync(
            With(AuditSort.ChangedAtDesc), new CursorRequest(2, page.NextCursor), CancellationToken.None)).Items);
        Assert.NotEmpty((await reader.ReadCellChangesAsync(
            With(AuditSort.ChangedAtAsc), new CursorRequest(2, "!!сміття!!"), CancellationToken.None)).Items);

        // Те саме для решти журналів: курсор журналу безпеки порядку «за часом» не годиться для «за користувачем».
        var secType = $"SortMismatch{Guid.NewGuid():N}"[..40];
        foreach (var s in seeds.Take(4))
        {
            await new AuditWriter(db).WriteSecurityEventAsync(
                new SecurityEventRecord(s.At, secType, null, null, null, s.UserId, null), CancellationToken.None);
        }

        var secPage = await reader.ReadSecurityEventsAsync(
            new SecurityEventFilter(At.AddMinutes(-1), At.AddHours(2), secType), new CursorRequest(2), CancellationToken.None);
        await Assert.ThrowsAsync<BusinessRuleException>(() => reader.ReadSecurityEventsAsync(
            new SecurityEventFilter(At.AddMinutes(-1), At.AddHours(2), secType, Sort: AuditSort.UserAsc),
            new CursorRequest(2, secPage.NextCursor), CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перша_сторінка_за_часом_читає_сталу_кількість_сторінок_незалежно_від_розміру_журналу()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            var writer = new AuditWriter(db);
            var batch = Enumerable.Range(0, 3000)
                .Select(i => Cell(doc, new Seed(i, At.AddSeconds(i), 1, string.Empty)))
                .ToList();
            await writer.WriteCellChangesAsync(batch, CancellationToken.None);
        }

        foreach (var sort in new[] { AuditSort.ChangedAtDesc, AuditSort.ChangedAtAsc })
        {
            var reads = await FirstPageReadsAsync(doc, sort);

            // ⛔ Стеля: сторінка з 51 рядка за кластерним ключем читає десяток сторінок БД, а не 3000 рядків вікна
            // (мутація «ORDER BY Id» або сортування вікна піднімає число на порядок).
            Assert.InRange(reads, 1, 60);
        }
    }

    private async Task<int> FirstPageReadsAsync(TestDocument doc, AuditSort sort)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        var messages = new List<string>();
        connection.InfoMessage += (_, args) => messages.Add(args.Message);

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET STATISTICS IO ON;";
            await setup.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        var filter = new CellChangeFilter(At.AddMinutes(-1), At.AddDays(1), DocumentId: doc.DocumentId, Sort: sort);
        var where = AuditReader.BuildCellChangeWhere(filter, command, after: null).ToString();
        command.CommandText = AuditReader.CellChangesHotSql(
            where,
            AuditPaging.Order(sort, "ChangedAt", "Id", "ChangedByUserId", "ChangedByUserId"),
            AuditPaging.OuterOrder(sort, "a", "ChangedAt", "Id", "ChangedByUserId", "ChangedByUserId"));
        command.Parameters.AddWithValue("@take", 51);
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        var total = 0;
        foreach (var message in messages)
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         message,
                         @"Table\s+'CellChange'\.\s+Scan count\s+\d+,\s+logical reads\s+(\d+)",
                         System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                         TimeSpan.FromSeconds(5)))
            {
                total += int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        Assert.True(total > 0, "У виводі SET STATISTICS IO немає рядка про aud.CellChange:" + string.Join('\n', messages));

        return total;
    }

    private static async Task<List<int>> WalkAsync<T>(Func<string?, Task<PagedResult<T>>> read, Func<T, int> key)
    {
        var keys = new List<int>();
        string? cursor = null;
        var guard = 0;
        do
        {
            var page = await read(cursor);
            keys.AddRange(page.Items.Select(key));
            cursor = page.NextCursor;
        }
        while (cursor is not null && ++guard < 100);

        return keys;
    }

    private static CellChangeRecord Cell(TestDocument doc, Seed s)
        => new(
            s.At,
            new CellAddress(new PeriodKey(202603), doc.RowIds[0], doc.ColumnDefIds[1]),
            doc.DocumentId,
            $"r{s.Index}",
            OldValue: "1",
            NewValue: "2",
            ChangedByUserId: s.UserId,
            Origin: "UserEdit",
            IsLateEdit: false,
            CorrelationId: null);

    private async Task InsertWorkflowEventAsync(TestDocument doc, Seed s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO wf.ApprovalEvent
                (DocumentId, SheetDefId, PeriodKey, FromStatus, ToStatus, Action, ByUserId, At, Reason, StepOrdinal)
            VALUES (@doc, @sheet, 202603, 1, 2, 4, @user, @at, @reason, NULL);
            """;
        command.Parameters.AddWithValue("@doc", doc.DocumentId);
        command.Parameters.AddWithValue("@sheet", doc.SheetDefId);
        command.Parameters.AddWithValue("@user", s.UserId);
        command.Parameters.Add("@at", System.Data.SqlDbType.DateTime2).Value = s.At;
        command.Parameters.AddWithValue("@reason", $"e{s.Index}");
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Чотири автори: двоє з однаковим іменем (розрізняє UserId), один — без імені-колізії; імена лише з літер.</summary>
    private async Task<IReadOnlyList<(int Id, string Name)>> UsersAsync()
    {
        var names = new[] { "Bravo", "Alpha", "Bravo", "Charlie" };
        var result = new List<(int, string)>();
        foreach (var name in names)
        {
            result.Add((await CreateUserAsync(name), name));
        }

        return result;
    }

    private async Task<int> CreateUserAsync(string displayName)
    {
        var unique = Guid.NewGuid().ToString("N");

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sec.[User]
                (UserName, DisplayName, Provider, WindowsSid, SecurityStamp, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@userName, @displayName, 0, @sid, @stamp, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@userName", $"sort-{unique}");
        command.Parameters.AddWithValue("@displayName", displayName);
        command.Parameters.AddWithValue("@sid", $"S-1-5-21-{unique}");
        command.Parameters.AddWithValue("@stamp", unique);

        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
