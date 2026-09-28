// tests/Ecr.Infrastructure.Tests/Persistence/RegistrySnapshotLoaderTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <see cref="RegistrySnapshotLoader"/> на РЕАЛЬНОМУ SQL Server (RT-22, ФВ-8.17,
/// FEATURE-REGISTRY-TABLES §5.7, <c>D-158</c>, <c>D-162</c>, <c>D-155</c>): бізнес-дата,
/// системний момент <c>AS OF</c>, видимість видалених і дітей невидимого батька, нормалізовані
/// ключі й стала кількість запитів.
/// </summary>
/// <remarks>
/// Фікстура — зменшений FLERT: <c>STREAM</c> → (композиція) <c>STREAM_CASE</c> → (композиція)
/// <c>GAS_COMPOSITION</c> → (посилання) <c>COMPONENT</c>. Кейс «370 Winter» потоку 1D-2 має дві
/// версії: закриту 15 червня (<c>ValidTo = 2026-06-15</c>, виключно) і чинну з 15 червня, з
/// власним складом кожна. Другий потік видалено разом із чинним кейсом під ним — рекурсивний
/// випадок видимості.
///
/// ⚠ Момент «до правки» — з годинника БАЗИ (<c>SYSUTCDATETIME()</c>), як у
/// <c>RegistryTemporalTests</c>: системний час пише SQL Server.
///
/// Мутаційні докази (RT-22, §9.2): прибрати перевірку батька в <c>RegistrySnapshot.Create</c>
/// (дитина видима, щойно видима сама) — червоніє
/// <see cref="Діти_невидимого_кейсу_невидимі"/>; читати в <c>RegistrySnapshotLoader</c> поточні
/// таблиці замість <c>TemporalAsOf</c> — червоніє <see cref="Правка_після_моменту_AS_OF_невидима"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistrySnapshotLoaderTests(SqlServerFixture sql)
{
    private static readonly DateOnly EndOfJune = new(2026, 6, 30);
    private static readonly DateOnly June14 = new(2026, 6, 14);
    private static readonly DateOnly June15 = new(2026, 6, 15);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-22")]
    [Trait("Requirement", "ФВ-8.17")]
    public async Task Запис_закритий_15_го_невидимий_на_кінець_місяця()
    {
        var f = await ArrangeAsync();

        var endOfMonth = await LoadAsync(f, EndOfJune);
        Assert.Equal([f.CaseOpen], endOfMonth.GetEntries(f.CaseCode)!);
        Assert.Equal(ExpressionErrors.BadReference, endOfMonth.GetField(f.CaseClosed, "T_C").ErrorCode);
        Assert.Equal(25m, endOfMonth.GetField(f.CaseOpen, "T_C").AsNumber());

        // Та сама база, інша бізнес-дата: 14 червня чинна ще стара версія.
        var before = await LoadAsync(f, June14);
        Assert.Equal([f.CaseClosed], before.GetEntries(f.CaseCode)!);
        Assert.Equal(20m, before.GetField(f.CaseClosed, "T_C").AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-22")]
    [Trait("Requirement", "ФВ-8.17")]
    public async Task Діти_невидимого_кейсу_невидимі()
    {
        var f = await ArrangeAsync();

        var snapshot = await LoadAsync(f, EndOfJune);

        // Склад закритого кейсу сам по собі видимий (нетемпоральний, не видалений), але його
        // батько закрився 15-го — і рядків немає ні в повному перегляді, ні в індексі.
        Assert.Equal(f.RowsOfOpenCase, snapshot.GetEntries(f.CompositionCode)!);
        Assert.Empty(snapshot.FindReferencing(f.CompositionCode, "CASE", f.CaseClosed)!);
        Assert.Equal(f.RowsOfOpenCase, snapshot.FindReferencing(f.CompositionCode, "CASE", f.CaseOpen)!);
        Assert.Equal(ExpressionErrors.BadReference, snapshot.GetField(f.RowsOfClosedCase[0], "MOL_PCT").ErrorCode);

        // Рекурсивно: потік видалено → його чинний кейс невидимий → склад цього кейсу теж.
        Assert.DoesNotContain(f.CaseOfDeletedStream, snapshot.GetEntries(f.CaseCode)!);
        Assert.DoesNotContain(f.RowOfDeletedStream, snapshot.GetEntries(f.CompositionCode)!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-22")]
    [Trait("Requirement", "ФВ-8.17")]
    public async Task Видалений_запис_відсутній_а_посилання_на_нього_REF()
    {
        var f = await ArrangeAsync();

        var snapshot = await LoadAsync(f, EndOfJune);

        // Порядок (Ordinal, Id): H2S має Ordinal 1, метан — 2; видалений CO відсутній.
        Assert.Equal([f.H2S, f.Methane], snapshot.GetEntries(f.ComponentCode)!);
        Assert.Equal(ExpressionErrors.BadReference, snapshot.GetField(f.DeletedCo, "MW").ErrorCode);
        Assert.Empty(snapshot.FindByPrimaryKey(f.ComponentCode, [ExpressionValue.Text("CO")])!);

        // Рядок складу, що посилається на видалений компонент, видимий (його батько — кейс), але
        // шлях ROW.COMPONENT дає #REF, а не id зниклого запису (§5.4).
        Assert.Equal(ExpressionErrors.BadReference, snapshot.GetField(f.RowWithDeletedComponent, "COMPONENT").ErrorCode);
        Assert.Equal(1m, snapshot.GetField(f.RowWithDeletedComponent, "MOL_PCT").AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-22")]
    [Trait("Requirement", "ФВ-8.17")]
    public async Task Правка_після_моменту_AS_OF_невидима()
    {
        var f = await ArrangeAsync();
        var moment = await DatabaseNowAsync();

        long added;
        await using (var db = sql.CreateContext())
        {
            var mw = await db.RegistryValues.SingleAsync(v => v.RegistryEntryId == f.Methane && v.RegistryFieldDefId == f.MwFieldId);
            mw.Set(CellDataType.Decimal, 16.5m, null);

            var tc = await db.RegistryValues.SingleAsync(v => v.RegistryEntryId == f.CaseOpen && v.RegistryFieldDefId == f.TcFieldId);
            tc.Set(CellDataType.Decimal, 30m, null);

            (await db.RegistryEntries.SingleAsync(e => e.Id == f.H2S)).SoftDelete();

            var nitrogen = new RegistryEntry(f.ComponentId, EcrCode.Create("N2"), Text("Nitrogen"), 1, DateTime.UtcNow);
            nitrogen.SetOrdinal(5);
            db.RegistryEntries.Add(nitrogen);
            await db.SaveChangesAsync();
            added = nitrogen.Id;
        }

        var then = await LoadAsync(f, EndOfJune, moment);
        Assert.Equal(16.043m, then.GetField(f.Methane, "MW").AsNumber());
        Assert.Equal(25m, then.GetField(f.CaseOpen, "T_C").AsNumber());
        Assert.Equal([f.H2S, f.Methane], then.GetEntries(f.ComponentCode)!);

        // Контроль: без моменту (прогін до системної історії) видно поточний стан — тобто правка
        // справді дійшла до бази, і різниця вище дана саме AS OF.
        var now = await LoadAsync(f, EndOfJune, asOfUtc: null);
        Assert.Equal(16.5m, now.GetField(f.Methane, "MW").AsNumber());
        Assert.Equal(30m, now.GetField(f.CaseOpen, "T_C").AsNumber());
        Assert.Equal([f.Methane, added], now.GetEntries(f.ComponentCode)!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-22")]
    [Trait("Requirement", "ФВ-8.17")]
    public async Task Ключ_нормалізовано_тим_самим_нормалізатором()
    {
        var f = await ArrangeAsync();
        var stream = ExpressionValue.Number(f.Stream1);

        // Збережено «370  Winter» (два пробіли); шукаємо « 370 winter » — §4.2: обрізка,
        // згортання пробілів, регістр.
        var endOfMonth = await LoadAsync(f, EndOfJune);
        Assert.Equal([f.CaseOpen], endOfMonth.FindByPrimaryKey(f.CaseCode, [stream, ExpressionValue.Text(" 370 winter ")])!);

        // Той самий ключ на 14 червня знаходить закриту версію: вікна не перетинаються (§4.4).
        var before = await LoadAsync(f, June14);
        Assert.Equal([f.CaseClosed], before.FindByPrimaryKey(f.CaseCode, [stream, ExpressionValue.Text("370 WINTER")])!);

        // Без первинного ключа — за кодом запису, однією частиною, без урахування регістру.
        Assert.Equal([f.H2S], endOfMonth.FindByPrimaryKey(f.ComponentCode, [ExpressionValue.Text(" h2s")])!);

        Assert.Empty(endOfMonth.FindByPrimaryKey(f.CaseCode, [stream, ExpressionValue.Text("370 Summer")])!);
        Assert.Null(endOfMonth.FindByPrimaryKey(f.CaseCode, [stream]));
        Assert.Null(endOfMonth.FindByPrimaryKey(f.CaseCode, [ExpressionValue.Text("1D-2"), ExpressionValue.Text("370 Winter")]));
        Assert.Null(endOfMonth.FindByPrimaryKey("NO_SUCH_" + f.Tag, [ExpressionValue.Text("X")]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-22")]
    public async Task Перелік_замикається_цілями_Lookup_і_шлях_проходить()
    {
        var f = await ArrangeAsync();

        // Формула прогону називає лише GAS_COMPOSITION.
        await using var db = sql.CreateContext();
        var snapshot = await new RegistrySnapshotLoader(db)
            .LoadAsync([f.CompositionId], EndOfJune, null, CancellationToken.None);

        Assert.NotNull(snapshot.GetEntries(f.ComponentCode));
        Assert.NotNull(snapshot.GetEntries(f.StreamCode));

        var row = f.RowsOfOpenCase[0];
        var component = snapshot.GetField(row, "component");
        Assert.Equal(ExpressionValueType.Number, component.Type);
        Assert.Equal(16.043m, snapshot.GetField((long)component.AsNumber()!.Value, "MW").AsNumber());
        Assert.Equal(ExpressionErrors.BadReference, snapshot.GetField(row, "NO_SUCH_FIELD").ErrorCode);
        Assert.Null(snapshot.FindReferencing(f.CompositionCode, "MOL_PCT", f.CaseOpen));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-22")]
    public async Task Кількість_запитів_не_залежить_від_обсягу()
    {
        var f = await ArrangeAsync();
        var moment = await DatabaseNowAsync();

        var small = await CountAsync(f, moment);

        // Ще п'ять кейсів по три рядки складу — утричі більше записів.
        await using (var db = sql.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                var @case = await AddEntryAsync(db, f.CaseId, $"X{i}", ordinal: 10 + i);
                await SetAsync(db, @case, f.CaseStreamFieldId, CellDataType.Lookup, f.Stream1);
                await SetAsync(db, @case, f.CaseNameFieldId, CellDataType.String, $"Extra {i}");

                for (var j = 0; j < 3; j++)
                {
                    var row = await AddEntryAsync(db, f.CompositionId, $"X{i}_{j}", ordinal: j);
                    await SetAsync(db, row, f.RowCaseFieldId, CellDataType.Lookup, @case);
                    await SetAsync(db, row, f.RowComponentFieldId, CellDataType.Lookup, f.Methane);
                    await SetAsync(db, row, f.MolPctFieldId, CellDataType.Decimal, 1m);
                }
            }
        }

        var large = await CountAsync(f, await DatabaseNowAsync());

        Assert.Equal(small.Commands, large.Commands);
        Assert.InRange(small.Commands, 1, 5);

        // Нуль і «стало» розрізняє лише вміст: більший знімок справді прочитано.
        Assert.Equal(small.Rows + 15, large.Rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-22")]
    public async Task Порожній_перелік_не_звертається_до_бази()
    {
        var counter = new DbCommandCounter();
        await using var db = CountingContext(counter);

        counter.Tally.Reset();
        var snapshot = await new RegistrySnapshotLoader(db).LoadAsync([], EndOfJune, null, CancellationToken.None);

        Assert.Equal(0, counter.Tally.Snapshot().Total);
        Assert.Null(snapshot.GetEntries("ANY"));
    }

    private async Task<(int Commands, int Rows)> CountAsync(Fixture f, DateTime moment)
    {
        var counter = new DbCommandCounter();
        await using var db = CountingContext(counter);

        counter.Tally.Reset();
        var snapshot = await new RegistrySnapshotLoader(db)
            .LoadAsync([f.CompositionId], EndOfJune, moment, CancellationToken.None);
        var commands = counter.Tally.Snapshot().Total;

        return (commands, snapshot.GetEntries(f.CompositionCode)!.Count);
    }

    private async Task<IRegistrySnapshot> LoadAsync(Fixture f, DateOnly businessDate, DateTime? asOfUtc = null)
    {
        await using var db = sql.CreateContext();
        return await new RegistrySnapshotLoader(db).LoadAsync(
            [f.StreamId, f.CaseId, f.CompositionId, f.ComponentId], businessDate, asOfUtc, CancellationToken.None);
    }

    /// <summary>Ідентифікатори фікстури.</summary>
    private sealed record Fixture(
        string Tag,
        int StreamId,
        int CaseId,
        int CompositionId,
        int ComponentId,
        int MwFieldId,
        int TcFieldId,
        int CaseStreamFieldId,
        int CaseNameFieldId,
        int RowCaseFieldId,
        int RowComponentFieldId,
        int MolPctFieldId,
        long Stream1,
        long CaseClosed,
        long CaseOpen,
        long CaseOfDeletedStream,
        long RowOfDeletedStream,
        long Methane,
        long H2S,
        long DeletedCo,
        long[] RowsOfClosedCase,
        long[] RowsOfOpenCase,
        long RowWithDeletedComponent)
    {
        public string StreamCode => $"STREAM_{Tag}";

        public string CaseCode => $"STREAM_CASE_{Tag}";

        public string CompositionCode => $"GAS_COMPOSITION_{Tag}";

        public string ComponentCode => $"COMPONENT_{Tag}";
    }

    private async Task<Fixture> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var component = await AddRegistryAsync(db, $"COMPONENT_{tag}", isTemporal: false);
        var mw = await AddFieldAsync(db, component, "MW", CellDataType.Decimal, 1);

        var stream = await AddRegistryAsync(db, $"STREAM_{tag}", isTemporal: false);
        var number = await AddFieldAsync(db, stream, "NUMBER", CellDataType.String, 1, required: true);
        await AddPrimaryKeyAsync(db, stream, number);

        // ⚠ Композиція STREAM → STREAM_CASE тут лише заради рекурсивного випадку видимості;
        // правила опису (дитина нетемпоральна, RT-12) завантажувач не перевіряє й не повинен.
        var @case = await AddRegistryAsync(db, $"STREAM_CASE_{tag}", isTemporal: true);
        var caseStream = await AddFieldAsync(db, @case, "STREAM", CellDataType.Lookup, 1, required: true, target: stream, composition: true);
        var caseName = await AddFieldAsync(db, @case, "CASE_NAME", CellDataType.String, 2, required: true);
        var tc = await AddFieldAsync(db, @case, "T_C", CellDataType.Decimal, 3);
        await AddPrimaryKeyAsync(db, @case, caseStream, caseName);

        var composition = await AddRegistryAsync(db, $"GAS_COMPOSITION_{tag}", isTemporal: false);
        var rowCase = await AddFieldAsync(db, composition, "CASE", CellDataType.Lookup, 1, required: true, target: @case, composition: true);
        var rowComponent = await AddFieldAsync(db, composition, "COMPONENT", CellDataType.Lookup, 2, required: true, target: component);
        var molPct = await AddFieldAsync(db, composition, "MOL_PCT", CellDataType.Decimal, 3);

        // Компоненти: H2S першим за Ordinal, CO видалено.
        var h2s = await AddEntryAsync(db, component.Id, "H2S", ordinal: 1);
        await SetAsync(db, h2s, mw.Id, CellDataType.Decimal, 34.08m);
        var methane = await AddEntryAsync(db, component.Id, "C1", ordinal: 2);
        await SetAsync(db, methane, mw.Id, CellDataType.Decimal, 16.043m);
        var co = await AddEntryAsync(db, component.Id, "CO", ordinal: 3);
        await SetAsync(db, co, mw.Id, CellDataType.Decimal, 28.01m);

        var stream1 = await AddEntryAsync(db, stream.Id, "S1", ordinal: 1);
        await SetAsync(db, stream1, number.Id, CellDataType.String, "1D-2");
        var stream2 = await AddEntryAsync(db, stream.Id, "S2", ordinal: 2);
        await SetAsync(db, stream2, number.Id, CellDataType.String, "1D-3");

        // Дві версії кейсу: [.., 15 червня) і [15 червня, ..).
        var closed = await AddEntryAsync(db, @case.Id, "K_OLD", ordinal: 1, to: June15);
        await SetAsync(db, closed, caseStream.Id, CellDataType.Lookup, stream1);
        await SetAsync(db, closed, caseName.Id, CellDataType.String, "370 Winter");
        await SetAsync(db, closed, tc.Id, CellDataType.Decimal, 20m);

        var open = await AddEntryAsync(db, @case.Id, "K_NEW", ordinal: 1, from: June15);
        await SetAsync(db, open, caseStream.Id, CellDataType.Lookup, stream1);
        await SetAsync(db, open, caseName.Id, CellDataType.String, "370  Winter");
        await SetAsync(db, open, tc.Id, CellDataType.Decimal, 25m);

        var orphanCase = await AddEntryAsync(db, @case.Id, "K_S2", ordinal: 2);
        await SetAsync(db, orphanCase, caseStream.Id, CellDataType.Lookup, stream2);
        await SetAsync(db, orphanCase, caseName.Id, CellDataType.String, "100 Summer");

        async Task<long> RowAsync(string code, int ordinal, long parent, long comp, decimal pct)
        {
            var row = await AddEntryAsync(db, composition.Id, code, ordinal);
            await SetAsync(db, row, rowCase.Id, CellDataType.Lookup, parent);
            await SetAsync(db, row, rowComponent.Id, CellDataType.Lookup, comp);
            await SetAsync(db, row, molPct.Id, CellDataType.Decimal, pct);
            return row;
        }

        long[] rowsClosed = [await RowAsync("R_OLD_1", 1, closed, methane, 90m), await RowAsync("R_OLD_2", 2, closed, h2s, 10m)];
        long[] rowsOpen =
        [
            await RowAsync("R_NEW_1", 1, open, methane, 95m),
            await RowAsync("R_NEW_2", 2, open, h2s, 4m),
            await RowAsync("R_NEW_3", 3, open, co, 1m),
        ];
        var orphanRow = await RowAsync("R_S2_1", 1, orphanCase, methane, 100m);

        // Видалення — після того, як на записи вже послалися, як у житті.
        (await db.RegistryEntries.SingleAsync(e => e.Id == co)).SoftDelete();
        (await db.RegistryEntries.SingleAsync(e => e.Id == stream2)).SoftDelete();
        await db.SaveChangesAsync();

        // ⚠ Наступна правка в ту саму мілісекунду дала б версію нульової тривалості
        // (datetime2(3)), і «станом на» не мав би моменту, коли видно саме вставлене.
        await Task.Delay(20);

        return new Fixture(
            tag, stream.Id, @case.Id, composition.Id, component.Id,
            mw.Id, tc.Id, caseStream.Id, caseName.Id, rowCase.Id, rowComponent.Id, molPct.Id,
            stream1, closed, open, orphanCase, orphanRow, methane, h2s, co,
            rowsClosed, rowsOpen, rowsOpen[2]);
    }

    private static async Task<RegistryDef> AddRegistryAsync(EcrDbContext db, string code, bool isTemporal)
    {
        var registry = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry;
    }

    private static async Task<RegistryFieldDef> AddFieldAsync(
        EcrDbContext db,
        RegistryDef registry,
        string code,
        CellDataType dataType,
        int ordinal,
        bool required = false,
        RegistryDef? target = null,
        bool composition = false)
    {
        var field = new RegistryFieldDef(registry.Id, EcrCode.Create(code), Text(code), dataType, ordinal);
        field.Update(Text(code), ordinal, required);
        if (target is not null)
        {
            field.PointTo(target.Id);
        }

        if (composition)
        {
            field.ComposeInto(ParentDeletePolicy.Cascade);
        }

        db.RegistryFieldDefs.Add(field);
        await db.SaveChangesAsync();
        return field;
    }

    private static async Task AddPrimaryKeyAsync(EcrDbContext db, RegistryDef registry, params RegistryFieldDef[] fields)
    {
        db.RegistryKeyDefs.Add(new RegistryKeyDef(
            registry.Id, EcrCode.Create("PK"), Text("PK"), fields, isPrimary: true, ignoreCase: true, 1, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private static async Task<long> AddEntryAsync(
        EcrDbContext db, int registryDefId, string code, int ordinal, DateOnly? from = null, DateOnly? to = null)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code), 1, DateTime.UtcNow);
        entry.SetOrdinal(ordinal);
        if (from is not null || to is not null)
        {
            entry.SetValidity(from, to);
        }

        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private static async Task SetAsync(EcrDbContext db, long entryId, int fieldId, CellDataType dataType, object value)
    {
        var registryValue = new RegistryValue(entryId, fieldId);
        registryValue.Set(dataType, value, null);
        db.RegistryValues.Add(registryValue);
        await db.SaveChangesAsync();
    }

    private async Task<DateTime> DatabaseNowAsync()
    {
        await using var db = sql.CreateContext();
        var now = Assert.Single(await db.Database
            .SqlQueryRaw<DateTime>("SELECT CAST(SYSUTCDATETIME() AS datetime2(3)) AS Value")
            .ToListAsync());
        await Task.Delay(20);
        return now;
    }

    private EcrDbContext CountingContext(DbCommandCounter counter)
        => new(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(counter))
            .Options);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
