// tests/Ecr.Infrastructure.Tests/Integration/IntegrationRowUpsertTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// Запис рядків від інтеграції з типізованими значеннями —
/// <see cref="ICellPatcher.ApplyIntegrationRowsAsync"/> (HSE301 A5a).
/// </summary>
/// <remarks>
/// ⚠ Усе справжнє й зібране контейнером, як у <c>MaterializeIntegrationActorTests</c>:
/// патчер, <c>PatchCellsHandler</c>, служба доступу, автор <c>svc-integration</c>
/// через <see cref="IntegrationActor"/>. Гонки відтворює перехоплювач
/// <see cref="IRowStore"/>: дія ПІСЛЯ заданого читання рядків (1 — план патчера,
/// 2 — контекст обробника).
///
/// ⚠ Таблиця <c>Mixed</c> на чотири колонки: текст, число, дата, Lookup на «свій»
/// довідник. Наявний рядок — перший рядок будівника; новий — ключ події
/// (<see cref="IntegrationRowUpsert.EventRowKey"/>).
/// </remarks>
[Collection("SqlServer")]
public sealed class IntegrationRowUpsertTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 1, 28, 14, 9, 20, DateTimeKind.Unspecified);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5a")]
    public async Task Новий_рядок_створюється_з_типізованими_значеннями_від_svc_integration()
    {
        var stand = await ArrangeAsync();
        var key = IntegrationRowUpsert.EventRowKey(Guid.NewGuid().ToString());

        var result = await RunAsync(stand, new RowStoreHook(), Row(key, stand, "V8", 269.258m, Start, stand.OwnEntryId));

        Assert.Equal(4, result.Applied);
        Assert.Empty(result.KeptManual);
        Assert.Null(result.WriteConflicts);
        Assert.Null(result.Rejected);

        var cells = await CellsAsync(stand, key);
        Assert.Equal("V8", cells[stand.Text].Text);
        Assert.Equal(269.258m, cells[stand.Number].Numeric);
        Assert.Equal(Start, cells[stand.Date].Date);
        Assert.Equal(stand.OwnEntryId, cells[stand.Lookup].EntryId);
        Assert.Equal($"{stand.SvcId}|Integration", await LastChangeAsync(stand, key, stand.Lookup));
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: <c>BaseVersion: null</c> і для наявного рядка в
    /// <c>IntegrationCellPatcher.PlanAsync</c> → обробник відмовляє
    /// <c>ECR-ROW-0409</c> <c>rowKeysExist</c> на кожній спробі, значення не
    /// записане, рядок у <c>WriteConflicts</c> — тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5a")]
    public async Task Наявний_рядок_оновлюється_без_rowKeysExist()
    {
        var stand = await ArrangeAsync();

        var result = await RunAsync(
            stand, new RowStoreHook(), new IntegrationRowUpsert(stand.ExistingKey, [Cell(stand.Number, IntegrationValue.Number(7m))]));

        Assert.Null(result.WriteConflicts);
        Assert.Equal(1, result.Applied);
        Assert.Equal(7m, (await CellsAsync(stand, stand.ExistingKey))[stand.Number].Numeric);
        Assert.Equal(1, await RowCountAsync(stand, stand.ExistingKey));
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>KeepManual</c> не відсівати <c>manual</c> → у
    /// комірці 7 від svc-integration; той самий помічник у методі комірок
    /// червонить <c>MaterializeIntegrationActorTests.Правка_людини_до_прогону_…</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-118")]
    public async Task Комірка_з_правкою_людини_лишається_і_йде_в_KeptManual()
    {
        var stand = await ArrangeAsync();
        var hook = new RowStoreHook();

        try
        {
            await using var provider = BuildProvider(hook);
            await HumanWriteAsync(provider, stand, stand.ExistingKey, new PatchCell(stand.NumberCode, 42m));

            var result = await UpsertAsync(
                provider,
                stand,
                new IntegrationRowUpsert(
                    stand.ExistingKey,
                    [Cell(stand.Number, IntegrationValue.Number(7m)), Cell(stand.Text, IntegrationValue.Text("V9"))]));

            Assert.Equal($"{stand.ExistingKey}:{stand.NumberCode}", Assert.Single(result.KeptManual));
            Assert.Equal(1, result.Applied);
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        var cells = await CellsAsync(stand, stand.ExistingKey);
        Assert.Equal(42m, cells[stand.Number].Numeric);
        Assert.Equal("V9", cells[stand.Text].Text);
        Assert.Equal($"{stand.HumanId}|UserEdit", await LastChangeAsync(stand, stand.ExistingKey, stand.Number));
    }

    /// <summary>Запис не з довідника колонки не пишеться, а решта рядка — пишеться.</summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>RejectedValuesAsync</c> не звіряти запис із
    /// довідником колонки → обробник відхиляє ВЕСЬ батч <c>ECR-CELL-4223</c>,
    /// виняток — тест червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5a")]
    [InlineData("foreign")]
    [InlineData("missing")]
    public async Task Lookup_не_з_довідника_колонки_не_пишеться_а_йде_в_Rejected(string which)
    {
        var stand = await ArrangeAsync();
        var key = IntegrationRowUpsert.EventRowKey(Guid.NewGuid().ToString());
        var entryId = which == "foreign" ? stand.ForeignEntryId : -1L;

        var result = await RunAsync(
            stand,
            new RowStoreHook(),
            new IntegrationRowUpsert(
                key, [Cell(stand.Number, IntegrationValue.Number(5m)), Cell(stand.Lookup, IntegrationValue.Lookup(entryId))]));

        Assert.Equal($"{key}:{stand.LookupCode}", Assert.Single(result.Rejected ?? []));
        Assert.Equal(1, result.Applied);

        var cells = await CellsAsync(stand, key);
        Assert.Equal(5m, cells[stand.Number].Numeric);
        Assert.False(cells.ContainsKey(stand.Lookup));
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати відсів незмінних значень у <c>PlanAsync</c>
    /// → другий виклик пише ті самі комірки: версія рядка росте, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5a")]
    public async Task Незмінене_значення_не_пишеться()
    {
        var stand = await ArrangeAsync();
        var key = IntegrationRowUpsert.EventRowKey(Guid.NewGuid().ToString());
        var row = Row(key, stand, "V8", 269.258m, Start, stand.OwnEntryId);

        await RunAsync(stand, new RowStoreHook(), row);
        var version = await RowVersionAsync(stand, key);
        var audit = await AuditCountAsync(stand, key);

        var second = await RunAsync(stand, new RowStoreHook(), row);

        Assert.Equal(0, second.Applied);
        Assert.Equal(version, await RowVersionAsync(stand, key));
        Assert.Equal(audit, await AuditCountAsync(stand, key));
    }

    /// <summary>
    /// Людина створює рядок із тим самим ключем між читанням і записом — результат
    /// оновлення, а не дубль і не падіння.
    /// </summary>
    /// <param name="afterRead">
    /// 1 — після читання патчера: обробник бачить рядок і відмовляє
    /// <c>rowKeysExist</c>; 2 — після читання обробника: рядок вставлено до його
    /// вставки, і дубль не пускає <c>UQ_TableRow_Key</c> (<c>rowKeyExists</c>).
    /// </param>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>IsCreatedMeanwhile</c> з умови повтору →
    /// випадок 2 падає винятком <c>ECR-ROW-0409</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5a")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Гонка_створення_з_людиною_дає_оновлення_без_дубля(int afterRead)
    {
        var stand = await ArrangeAsync();
        var key = IntegrationRowUpsert.EventRowKey(Guid.NewGuid().ToString());
        var hook = new RowStoreHook();
        IntegrationWriteResult result;

        try
        {
            await using var provider = BuildProvider(hook);
            hook.AfterRead = async () =>
            {
                if (hook.Reads == afterRead)
                {
                    hook.AfterRead = null;
                    await HumanWriteAsync(provider, stand, key, new PatchCell(stand.NumberCode, 42m));
                }
            };

            result = await UpsertAsync(
                provider,
                stand,
                new IntegrationRowUpsert(
                    key, [Cell(stand.Number, IntegrationValue.Number(7m)), Cell(stand.Text, IntegrationValue.Text("V8"))]));
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }

        Assert.Null(result.WriteConflicts);
        Assert.Equal($"{key}:{stand.NumberCode}", Assert.Single(result.KeptManual));
        Assert.Equal(1, await RowCountAsync(stand, key));

        var cells = await CellsAsync(stand, key);
        Assert.Equal(42m, cells[stand.Number].Numeric);
        Assert.Equal("V8", cells[stand.Text].Text);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5a")]
    [InlineData("6f9619ff-8b86-d011-b42d-00c04fc964ff", "EF-6f9619ff-8b86-d011-b42d-00c04fc964ff")]
    [InlineData("E1", "EF-E1")]
    public void Ключ_події_EF_плюс_ID_якщо_проходить_шаблон(string eventId, string expected)
        => Assert.Equal(expected, IntegrationRowUpsert.EventRowKey(eventId));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A5a")]
    public void Ключ_події_з_недопустимим_ID_EF_плюс_32_hex_і_детермінований()
    {
        var key = IntegrationRowUpsert.EventRowKey("\\\\PI\\Events[a b]");

        Assert.Matches("^EF-[0-9a-f]{32}$", key);
        Assert.True(RowKey.TryCreate(key, out _));
        Assert.Equal(key, IntegrationRowUpsert.EventRowKey("\\\\PI\\Events[a b]"));
        Assert.NotEqual(key, IntegrationRowUpsert.EventRowKey("\\\\PI\\Events[a c]"));
        Assert.Matches("^EF-[0-9a-f]{32}$", IntegrationRowUpsert.EventRowKey(new string('a', 98)));
    }

    // ── Стенд ────────────────────────────────────────────────────────────────

    /// <summary>Усе, що заведено для одного тесту.</summary>
    private sealed record Stand(
        TestDocument Chain,
        int SvcId,
        int RoleId,
        int HumanId,
        string ExistingKey,
        int Text,
        int Number,
        string NumberCode,
        int Date,
        int Lookup,
        string LookupCode,
        long OwnEntryId,
        long ForeignEntryId);

    private static IntegrationRowCell Cell(int columnDefId, IntegrationValue value) => new(columnDefId, value);

    private static IntegrationRowUpsert Row(string key, Stand stand, string text, decimal number, DateTime date, long entryId)
        => new(
            key,
            [
                Cell(stand.Text, IntegrationValue.Text(text)),
                Cell(stand.Number, IntegrationValue.Number(number)),
                Cell(stand.Date, IntegrationValue.Date(date)),
                Cell(stand.Lookup, IntegrationValue.Lookup(entryId)),
            ]);

    /// <summary>
    /// Ланцюг <c>Mixed</c> із відкритим періодом; колонки 3 і 4 переведено в
    /// <c>Date</c> і <c>Lookup</c> («свій» довідник); запис «чужого» довідника;
    /// людина з грантом <c>Write</c> (роль знімається у <c>finally</c>/<see cref="RunAsync"/>).
    /// </summary>
    private async Task<Stand> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(columnCount: 4, rowMode: TableRowMode.Mixed, ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);

        var tag = Guid.NewGuid().ToString("N")[..8];
        var own = new RegistryDef(EcrCode.Create($"A5OWN_{tag}"), Name("own"), isTemporal: false);
        var foreign = new RegistryDef(EcrCode.Create($"A5FOR_{tag}"), Name("foreign"), isTemporal: false);
        db.RegistryDefs.AddRange(own, foreign);
        await db.SaveChangesAsync(CancellationToken.None);

        var ownEntry = new RegistryEntry(own.Id, EcrCode.Create("V8"), Name("V8"));
        var foreignEntry = new RegistryEntry(foreign.Id, EcrCode.Create("V8"), Name("V8"));
        db.RegistryEntries.AddRange(ownEntry, foreignEntry);
        await db.SaveChangesAsync(CancellationToken.None);

        var ids = chain.ColumnDefIds;
        await ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET DataType = {(int)CellDataType.Date} WHERE Id = {ids[2]}; "
            + $"UPDATE cfg.ColumnDef SET DataType = {(int)CellDataType.Lookup}, LookupRegistryDefId = {own.Id} WHERE Id = {ids[3]};");

        var codes = await db.ColumnDefs.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code);
        var existingKey = (await db.TableRows
            .Where(r => r.PeriodKeyValue == chain.PeriodKey.Value && r.Id == chain.RowIds[0])
            .Select(r => r.RowKey)
            .SingleAsync()).Value;

        var roleId = await CreateWriterRoleAsync(db, chain.ProjectId);
        var humanId = await AddHumanAsync(db, roleId);
        var svcId = Convert.ToInt32(
            await ScalarAsync($"SELECT Id FROM sec.[User] WHERE UserName = N'{IntegrationActor.UserName}'"),
            System.Globalization.CultureInfo.InvariantCulture);

        return new Stand(
            chain, svcId, roleId, humanId, existingKey,
            Text: ids[0], Number: ids[1], NumberCode: codes[ids[1]], Date: ids[2], Lookup: ids[3], LookupCode: codes[ids[3]],
            OwnEntryId: ownEntry.Id, ForeignEntryId: foreignEntry.Id);
    }

    /// <summary>Один виклик патчера у власному контейнері; роль людини знімається.</summary>
    private async Task<IntegrationWriteResult> RunAsync(Stand stand, RowStoreHook hook, params IntegrationRowUpsert[] rows)
    {
        try
        {
            await using var provider = BuildProvider(hook);
            return await UpsertAsync(provider, stand, rows);
        }
        finally
        {
            await RevokeAsync(stand.RoleId);
        }
    }

    /// <summary>Запис рядків від імені <c>svc-integration</c>, як у задачі.</summary>
    private static async Task<IntegrationWriteResult> UpsertAsync(
        ServiceProvider provider, Stand stand, params IntegrationRowUpsert[] rows)
    {
        await using var scope = provider.CreateAsyncScope();
        using var author = await scope.ServiceProvider.GetRequiredService<IntegrationActor>().EnterAsync(CancellationToken.None);

        return await scope.ServiceProvider.GetRequiredService<ICellPatcher>().ApplyIntegrationRowsAsync(
            stand.Chain.DocumentId, stand.Chain.TableInstanceId, stand.Chain.PeriodKey, rows, CancellationToken.None);
    }

    /// <summary>
    /// Правка людини через той самий обробник, у власному scope: наявний рядок —
    /// за його версією, відсутній — створення (<c>null</c>).
    /// </summary>
    private static async Task HumanWriteAsync(ServiceProvider provider, Stand stand, string rowKey, PatchCell cell)
    {
        await using var scope = provider.CreateAsyncScope();
        using var author = scope.ServiceProvider.GetRequiredService<JobActorScope>()
            .Enter(new JobActor(stand.HumanId, "human", "en", [], Guid.NewGuid().ToString("N")));

        var rows = await scope.ServiceProvider.GetRequiredService<IRowStore>()
            .GetRowsAsync(stand.Chain.TableInstanceId, stand.Chain.PeriodKey, CancellationToken.None);
        var version = rows.SingleOrDefault(r => r.RowKey == rowKey)?.RowVersion;

        await scope.ServiceProvider.GetRequiredService<PatchCellsHandler>().HandleAsync(
            new PatchCellsRequest(
                stand.Chain.TableInstanceId, stand.Chain.PeriodKey.Value, CellChangeOrigins.UserEdit,
                [new PatchRow(rowKey, version, [cell])]),
            CancellationToken.None);
    }

    /// <summary>Контейнер як у проді: застосунок + інфраструктура + обгортка автора задачі.</summary>
    private ServiceProvider BuildProvider(RowStoreHook hook)
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

        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));

        // ⚠ Справжнє сховище рядків із дією після читання — і для патчера, і для обробника.
        services.AddScoped<RowStore>();
        services.AddScoped<IRowStore>(sp => new InterceptingRowStore(sp.GetRequiredService<RowStore>(), hook));

        // ⚠ Черга перерахунку — підробка: Quartz тримає планувальник у
        // глобальному репозиторії процесу (див. MaterializeIntegrationActorTests).
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider();
    }

    private static async Task<int> CreateWriterRoleAsync(EcrDbContext db, int projectId)
    {
        var role = new Role(
            EcrCode.Create($"A5A{Guid.NewGuid():N}"[..16]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "A5a human writer (test)" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync(CancellationToken.None);

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync(CancellationToken.None);

        return role.Id;
    }

    private static async Task<int> AddHumanAsync(EcrDbContext db, int roleId)
    {
        var human = new User($"human_{Guid.NewGuid():N}"[..20], "Test human", AuthProvider.Local);
        human.SetPassword("not-a-real-hash"); // CK_User_Provider: локальному — хеш.
        db.Users.Add(human);
        await db.SaveChangesAsync(CancellationToken.None);

        db.RoleAssignments.Add(new RoleAssignment(roleId, human.Id, principalSid: null));
        await db.SaveChangesAsync(CancellationToken.None);

        return human.Id;
    }

    private Task RevokeAsync(int roleId)
        => ExecuteAsync(
            $"DELETE FROM sec.ResourceGrant WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.RoleAssignment WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.Role WHERE Id = {roleId};");

    // ── Читання стану ────────────────────────────────────────────────────────

    /// <summary>Збережене значення комірки.</summary>
    private sealed record Stored(string? Text, decimal? Numeric, DateTime? Date, long? EntryId);

    private async Task<Dictionary<int, Stored>> CellsAsync(Stand stand, string rowKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT v.ColumnDefId, v.ValueString, v.ValueNumeric, v.ValueDate, v.ValueRegistryEntryId "
            + "FROM doc.CellValue v JOIN doc.TableRow r ON r.PeriodKey = v.PeriodKey AND r.Id = v.TableRowId "
            + "WHERE r.PeriodKey = @period AND r.TableInstanceId = @instance AND r.RowKey = @rowKey";
        command.Parameters.AddWithValue("@period", stand.Chain.PeriodKey.Value);
        command.Parameters.AddWithValue("@instance", stand.Chain.TableInstanceId);
        command.Parameters.AddWithValue("@rowKey", rowKey);
        await using var reader = await command.ExecuteReaderAsync();

        var result = new Dictionary<int, Stored>();
        while (await reader.ReadAsync())
        {
            result[reader.GetInt32(0)] = new Stored(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4));
        }

        return result;
    }

    private async Task<int> RowCountAsync(Stand stand, string rowKey)
        => Convert.ToInt32(
            await ScalarAsync(
                $"SELECT COUNT(*) FROM doc.TableRow WHERE PeriodKey = {stand.Chain.PeriodKey.Value} "
                + $"AND TableInstanceId = {stand.Chain.TableInstanceId} AND RowKey = N'{rowKey}'"),
            System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> RowVersionAsync(Stand stand, string rowKey)
        => Convert.ToBase64String((byte[])(await ScalarAsync(
            $"SELECT RowVersion FROM doc.TableRow WHERE PeriodKey = {stand.Chain.PeriodKey.Value} "
            + $"AND TableInstanceId = {stand.Chain.TableInstanceId} AND RowKey = N'{rowKey}'"))!);

    private async Task<int> AuditCountAsync(Stand stand, string rowKey)
        => Convert.ToInt32(
            await ScalarAsync($"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {stand.Chain.DocumentId} AND RowKey = N'{rowKey}'"),
            System.Globalization.CultureInfo.InvariantCulture);

    private Task<object?> LastChangeAsync(Stand stand, string rowKey, int columnDefId)
        => ScalarAsync(
            $"SELECT TOP 1 CONCAT(ChangedByUserId, N'|', Origin) FROM aud.CellChange "
            + $"WHERE DocumentId = {stand.Chain.DocumentId} AND RowKey = N'{rowKey}' AND ColumnDefId = {columnDefId} ORDER BY Id DESC");

    private async Task<object?> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    // ── Перехоплювач читань рядків ───────────────────────────────────────────

    /// <summary>Дія після читання рядків і лічильник читань поза нею.</summary>
    private sealed class RowStoreHook
    {
        /// <summary>Стеля читань: нескінченний повтор має впасти тут, а не зависнути.</summary>
        private const int MaxReads = 20;

        private bool _inside;

        /// <summary>Дія після кожного читання рядків; <c>null</c> — прозоро.</summary>
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

    /// <summary><c>Ecr.Api.Auth.CurrentUser</c> поза HTTP-запитом: анонімний.</summary>
    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
