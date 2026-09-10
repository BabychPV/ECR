using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// Правила доступу до періоду на реальному зрізі.
/// </summary>
/// <remarks>
/// ⛔ Ці тести перевіряють не самі правила — їх перевіряє
/// <c>PeriodAccessRuleTests</c> без бази, — а те, що правила ВЗАГАЛІ хтось
/// викликає. Чиста функція з вісімнадцятьма зеленими тестами, яку не кличе
/// жоден бойовий шлях, — це той самий дефект, що весь <c>A7</c>: механізм
/// оголошений і недосяжний.
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodAccessSliceTests(SqlServerFixture sql) : IDisposable
{
    /// <summary>Період зрізу.</summary>
    /// <remarks>
    /// ⚠ Не 2026-05…07: ті періоди архівують <c>ArchiveJobTests</c>, а
    /// звільнення партиції йде ПО ПЕРІОДУ і не знає про проєкти. Тести з
    /// одним ключем періоду в спільній базі знищують дані один одному, і
    /// падає при цьому не той, хто винен.
    /// </remarks>
    private const int PeriodKeyValue = 202609;

    private const byte MonthNumber = 9;

    private static readonly DateTime Now = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.4")]
    public async Task Поданий_аркуш_робить_комірки_зрізу_нередагованими()
    {
        // ⛔ Знахідка `A7-51`. `CanEditSliceAsync` будував умови з
        // `sheetDefId: null`, тому стан робочого процесу не читався взагалі й
        // підставлявся як `Draft`. Перевірка в `EditRules` була, тести на неї
        // були — а на бойовому шляху запису (`PatchCellsHandler` кличе саме
        // зріз) вона не спрацьовувала ЖОДНОГО разу: подану форму можна було
        // правити далі.
        var (doc, builder) = await ArrangeAsync();

        await using (var db = builder.CreateContext())
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, PeriodKeyValue);
            state.Submit(userId: 1, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var decisions = await DecideAsync(builder, doc);

        Assert.NotEmpty(decisions);
        Assert.All(decisions.Values, d =>
        {
            Assert.False(d.IsAllowed);
            Assert.Equal(EditDenyReason.DocumentSubmitted, d.Reason);
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.20")]
    public async Task Місяць_поза_вікном_дії_дозволу_не_редагується()
    {
        // Дозвіл діє до 31 серпня; зріз — за вересень. Число за вересень
        // означало б заявлений викид без чинного дозволу — рівно те, за що
        // штрафує регулятор, і рівно те, що чинне рішення блокувало
        // процедурою `ApplyPermitMonthLocks`.
        var (doc, builder) = await ArrangeAsync();

        await ArrangePermitAsync(
            builder, doc,
            validFrom: new DateOnly(2026, 1, 1),
            validTo: new DateOnly(2026, 8, 31),
            forRows: [doc.RowIds[0]]);

        var decisions = await DecideAsync(builder, doc);
        var monthColumn = doc.ColumnDefIds[2];

        var blocked = decisions[new CellAddress(new PeriodKey(PeriodKeyValue), doc.RowIds[0], monthColumn)];

        Assert.False(blocked.IsAllowed);
        Assert.Equal(EditDenyReason.OutsidePermitWindow, blocked.Reason);

        // ⚠ Рядок, який дозволу не обрав, лишається редагованим. Зворотне
        // зробило б заповнення таблиці неможливим у принципі: щоб обрати
        // дозвіл, треба мати право написати в рядок.
        var free = decisions[new CellAddress(new PeriodKey(PeriodKeyValue), doc.RowIds[1], monthColumn)];

        Assert.True(free.IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task AllowWithConfirmation_дозволяє_комірку_але_вимагає_підтвердження()
    {
        // ⛔ `#43`. До цієї гілки `AllowWithConfirmation` рахувався
        // (`PeriodAccessRules.Evaluate` повертав правильну `Behavior`), але
        // результат ніхто не читав: `Decide()` бачив, що правило НЕ блокує
        // (`outcome.Blocks == false` — те саме, що й для `Warn`), і рішення
        // лишалося звичайним `Allow()` без жодного сліду того, що правило
        // взагалі спрацювало. Клієнт не мав чим відрізнити цю комірку від
        // будь-якої іншої дозволеної.
        var (doc, builder) = await ArrangeAsync();

        await ArrangePermitAsync(
            builder, doc,
            validFrom: new DateOnly(2026, 1, 1),
            validTo: new DateOnly(2026, 8, 31),
            forRows: [doc.RowIds[0]],
            behavior: OutOfWindowBehavior.AllowWithConfirmation);

        var decisions = await DecideAsync(builder, doc);
        var monthColumn = doc.ColumnDefIds[2];

        var decision = decisions[
            new CellAddress(new PeriodKey(PeriodKeyValue), doc.RowIds[0], monthColumn)];

        // Дозволено — комірка НЕ сіра, на відміну від `ReadOnly` вище.
        Assert.True(decision.IsAllowed);
        Assert.Equal(EditDenyReason.None, decision.Reason);

        // Але позначено як таке, що потребує явного підтвердження, і з
        // поясненням, яке підуть у діалог — не голе "true".
        Assert.True(decision.RequiresConfirmation);
        Assert.False(string.IsNullOrWhiteSpace(decision.Detail));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.20")]
    public async Task Місяць_у_межах_дії_дозволу_редагується()
    {
        // Дозвіл закінчується 15 вересня — вересень він усе-таки покриває.
        // Порівняння місяця з датою «як дати» відрізало б півмісяця реальних
        // даних, і помітили б це лише наприкінці кварталу.
        var (doc, builder) = await ArrangeAsync();

        await ArrangePermitAsync(
            builder, doc,
            validFrom: new DateOnly(2026, 1, 1),
            validTo: new DateOnly(2026, 9, 15),
            forRows: [doc.RowIds[0]]);

        var decisions = await DecideAsync(builder, doc);

        var decision = decisions[
            new CellAddress(new PeriodKey(PeriodKeyValue), doc.RowIds[0], doc.ColumnDefIds[2])];

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.10")]
    public async Task Правило_SourceWindow_не_додає_запитів_на_кожен_рядок()
    {
        // ⛔ Головний тест кроку. Спокуса реалізувати `SourceWindow` походом у
        // довідник на кожну комірку велика, і вона ТИХА: поведінкові тести
        // вище пройдуть однаково, а бюджет 50 мс (`ФВ-6.10`) впаде лише на
        // реальному зрізі 500×60 — тобто в продуктиві.
        //
        // Тому міряється не час (він плаває), а кількість команд до SQL, і не
        // її абсолютне значення, а ЗАЛЕЖНІСТЬ від кількості рядків. Запит на
        // комірку дав би різницю в десятки.
        var small = await CountCommandsAsync(rowCount: 3);
        var large = await CountCommandsAsync(rowCount: 30);

        Assert.Equal(small, large);
    }

    /// <summary>Скільки команд до SQL коштує зріз із правилом <c>SourceWindow</c>.</summary>
    private async Task<int> CountCommandsAsync(int rowCount)
    {
        var (doc, builder) = await ArrangeAsync(rowCount);

        await ArrangePermitAsync(
            builder, doc,
            validFrom: new DateOnly(2026, 1, 1),
            validTo: new DateOnly(2026, 8, 31),
            forRows: doc.RowIds);

        var executed = new List<string>();

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);

        _ = await Service(db).CanEditSliceAsync(
            Profile(doc.ProjectId), doc.TableInstanceId, CancellationToken.None);

        return executed.Count;
    }

    /// <summary>Ланцюг «шаблон → період → документ» із відкритим періодом.</summary>
    private async Task<(TestDocument Document, TestDocumentBuilder Builder)> ArrangeAsync(int rowCount = 3)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            PeriodKeyValue, columnCount: 3, rowCount: rowCount, ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            // Період відкривається явно: `new Period(...)` створює його
            // `Scheduled`, і без цього кроку кожна комірка відмовляла б із
            // `PeriodNotOpenYet` — тобто тест перевіряв би не те.
            var period = await db.Periods
                .FirstAsync(
                    p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue,
                    CancellationToken.None);

            period.AdvanceTo(PeriodState.Open, Now);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Третя колонка стає місячною: правило `SourceWindow` — про місяці, і
        // без цієї ознаки воно не має до чого прикластися.
        await ExecuteAsync(
            "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = @month WHERE Id = @id",
            ("@id", doc.ColumnDefIds[2]), ("@month", MonthNumber));

        return (doc, builder);
    }

    /// <summary>Довідник дозволів, правило вікна і посилання рядків на запис.</summary>
    private static async Task ArrangePermitAsync(
        TestDocumentBuilder builder,
        TestDocument doc,
        DateOnly validFrom,
        DateOnly validTo,
        IReadOnlyList<long> forRows,
        OutOfWindowBehavior behavior = OutOfWindowBehavior.ReadOnly)
    {
        await using var db = builder.CreateContext();

        var registry = new RegistryDef(
            EcrCode.Create($"PERMIT_{doc.TemplateVersionId}"), Text("Permits"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(CancellationToken.None);

        var entry = new RegistryEntry(registry.Id, EcrCode.Create("P1"), Text("Permit 1"));
        entry.SetValidity(validFrom, validTo);
        db.RegistryEntries.Add(entry);

        // Правило вказує на ПЕРШУ колонку — ту, де рядок обирає дозвіл.
        // Блокує воно при цьому місячні колонки, а не себе саме.
        var rule = PeriodAccessRuleDef
            .ForSourceWindow(doc.TemplateVersionId, doc.ColumnDefIds[0], behavior)
            .ForTable(doc.TableDefId);

        db.PeriodAccessRules.Add(rule);
        await db.SaveChangesAsync(CancellationToken.None);

        foreach (var rowId in forRows)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(new PeriodKey(PeriodKeyValue), rowId, doc.ColumnDefIds[0]),
                doc.TableDefId,
                new CellValueData { ValueRegistryEntryId = (int)entry.Id }));
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Рішення про весь зріз.</summary>
    private async Task<IReadOnlyDictionary<CellAddress, EditDecision>> DecideAsync(
        TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();

        return await Service(db).CanEditSliceAsync(
            Profile(doc.ProjectId), doc.TableInstanceId, CancellationToken.None);
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    /// <summary>Профіль із правом писати в проєкт: предмет тестів — правила, не гранти.</summary>
    private static AccessProfile Profile(int projectId)
        => new AccessBuilder()
            .Grant(ResourceKind.Project, projectId, GrantLevel.Write)
            .Build();

    private async Task ExecuteAsync(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
