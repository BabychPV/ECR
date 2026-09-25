using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перерахунок формул шаблону, що перетинається в часі з поданням того самого
/// аркуша (<c>SubmitEditRaceTests</c> — та сама гонка для правки людини).
/// </summary>
/// <remarks>
/// ⛔ Інваріант, який тримає файл: <b>обчислена комірка в
/// <c>calc.SubmissionSnapshot</c> дорівнює живій обчисленій комірці після того,
/// як обидві дії завершились</b>. Або перерахунок зайшов у зріз (записав ДО того,
/// як подання прочитало аркуш), або чекав і після подання поданих чисел не
/// змінив. Правило продукту — ФВ-9.17 (<c>RecalculationWritePolicy</c>): поданий
/// аркуш перерахунком не переписується, шлях один — Reopen.
///
/// ⚠ Гонка відтворюється ДЕТЕРМІНОВАНО, двома точками перемикання:
/// <list type="number">
/// <item>подання вже прочитало зріз, але ще не зафіксоване (точка —
/// <c>IAccessDecisionService.CurrentApprovalStepAsync</c> усередині транзакції
/// подання), — і в цей момент стартує перерахунок;</item>
/// <item>перерахунок уже порахував нові значення з даних <c>Draft</c>-аркуша, а
/// транзакцію запису ще не відкрив (точка — <c>IPeriodStore.FindPeriodStateAsync</c>,
/// яку <c>RecalculationService.RunAsync</c> кличе ПІСЛЯ обчислення і ДО
/// транзакції), — і в цей момент подання проходить повністю.</item>
/// </list>
///
/// ⚠ Дані: вхід <c>IN = 21</c>, обчислена <c>OUT = 20</c> лежить від попереднього
/// перерахунку (<c>IN</c> змінили, а каскадна задача ще в черзі — звичайний стан
/// між збереженням і фоновим перерахунком). Формула <c>OUT = IN * 2</c> дає 42.
///
/// ⚠ Подання саме перераховує свій аркуш під винятковим блокуванням
/// (<c>ISubmitRecalculation</c>), тому другий інваріант — <b>подане OUT = подане
/// IN × 2</b>. Щоб фоновий перерахунок у порядку «порахував до подання, пише
/// після» ніс ІНШЕ число, ніж подане, між його обчисленням і поданням вхід
/// змінює друга правка (IN = 30).
///
/// ⚠ Подання й перерахунок мають ВЛАСНІ <c>EcrDbContext</c> — інакше гонки не
/// було б.
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitRecalculationRaceTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    /// <summary>Вхід формули в базі на момент гонки.</summary>
    private const decimal InputValue = 21m;

    /// <summary>Обчислене значення від попереднього перерахунку (застаріле).</summary>
    private const decimal StaleOutput = 20m;

    /// <summary>Що порахує формула <c>[IN] * 2</c> з поточного входу.</summary>
    private const decimal FreshOutput = 42m;

    /// <summary>Вхід після другої правки (порядок «порахував до подання, пише після»).</summary>
    private const decimal SecondInput = 30m;

    /// <summary>Ідентифікатор формули в підставному знімку (у <c>cfg.*</c> її немає — і не треба).</summary>
    private const int FormulaId = 910_001;

    /// <summary>Скільки чекати, поки перерахунок сам дійде до кінця або стане в чергу за поданням.</summary>
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromSeconds(2);

    /// <summary>Стеля на будь-яке очікування точки перемикання — щоб зламаний тест падав, а не висів.</summary>
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Перерахунок_поки_транзакція_подання_відкрита_не_розводить_зріз_і_живу_комірку()
    {
        var doc = await ArrangeAsync();

        await using var submitDb = CreateContext();
        await using var recalcDb = CreateContext();

        var submitInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubmit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var submit = BuildSubmit(submitDb, doc, async () =>
        {
            submitInside.TrySetResult();
            await releaseSubmit.Task.WaitAsync(HookTimeout).ConfigureAwait(false);
        });
        var recalc = BuildRecalculation(recalcDb, doc, beforeWrite: null);

        var submitTask = Task.Run(() => submit.HandleAsync(
            doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None));
        await submitInside.Task.WaitAsync(HookTimeout);

        // Подання стоїть усередині своєї транзакції: аркуш перераховано й зріз
        // прочитано, коміту ще немає. Перерахунок стартує саме тепер і має час або дописати
        // до кінця, або стати в чергу за поданням.
        var recalcTask = Task.Run(() => recalc.RecalculateAllAsync(
            doc.DocumentId, doc.PeriodKey, CancellationToken.None));
        await Task.WhenAny(recalcTask, Task.Delay(OverlapWindow));

        releaseSubmit.TrySetResult();
        await submitTask.WaitAsync(HookTimeout);
        await recalcTask.WaitAsync(HookTimeout);

        await AssertSnapshotMatchesLiveAsync(doc);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Перерахунок_що_порахував_до_подання_а_пише_після_не_змінює_поданих_чисел()
    {
        var doc = await ArrangeAsync();

        await using var submitDb = CreateContext();
        await using var recalcDb = CreateContext();

        var recalcComputed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecalc = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var submit = BuildSubmit(submitDb, doc, insideTransaction: null);
        var recalc = BuildRecalculation(recalcDb, doc, async () =>
        {
            recalcComputed.TrySetResult();
            await releaseRecalc.Task.WaitAsync(HookTimeout).ConfigureAwait(false);
        });

        var recalcTask = Task.Run(() => recalc.RecalculateAllAsync(
            doc.DocumentId, doc.PeriodKey, CancellationToken.None));
        await recalcComputed.Task.WaitAsync(HookTimeout);

        // Перерахунок порахував OUT = 42 з IN = 21. Тепер вхід змінює ще одна
        // зафіксована правка (IN = 30; її власна задача теж у черзі), і подання
        // проходить повністю, до коміту, — сам рахує OUT = 60 і подає його. Лише
        // потім перший перерахунок пише своє застаріле 42.
        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = {SecondInput.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"WHERE PeriodKey = {doc.PeriodKey.Value} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]}");

        await submit.HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None)
                    .WaitAsync(HookTimeout);

        releaseRecalc.TrySetResult();
        await recalcTask.WaitAsync(HookTimeout);

        await AssertSnapshotMatchesLiveAsync(doc);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Подання_одразу_після_правки_входу_подає_обчислене_з_нового_входу()
    {
        // ⛔ Стан «правку входу зафіксовано, каскадна задача ще в черзі»:
        // `PatchCellsHandler` сам не рахує, він ставить `FormulaRecalculationJob`
        // ПІСЛЯ коміту, — тож у базі IN = 21 і застаріле OUT = 20. Людина одразу
        // натискає «Подати». Жодного фонового перерахунку тут не запускається.
        var doc = await ArrangeAsync();

        await using var submitDb = CreateContext();
        var submit = BuildSubmit(submitDb, doc, insideTransaction: null);

        await submit.HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None)
                    .WaitAsync(HookTimeout);

        // До виправлення зріз фіксував OUT = 20 при IN = 21, і задача з черги
        // після подання поданий аркуш уже пропускала — застаріле число навічно.
        var (input, output) = await SubmittedAsync(doc);
        Assert.Equal(InputValue, input);
        Assert.True(
            output == FreshOutput,
            $"Зріз подання: IN = {input}, OUT = {output}; формула OUT = IN * 2 вимагає {FreshOutput}: " +
            "подання заморозило обчислене число, пораховане зі старого входу.");
        Assert.Equal(FreshOutput, await LiveOutputAsync(doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Перерахунок_неподаного_аркуша_під_блокуванням_пише_як_і_раніше()
    {
        // ⚠ Контроль межі: блокування і перевірка стану не мають вимкнути
        // перерахунок узагалі. Аркуш не поданий — формула рахується й пишеться.
        var doc = await ArrangeAsync();

        await using var recalcDb = CreateContext();
        var recalc = BuildRecalculation(recalcDb, doc, beforeWrite: null);

        var written = await recalc.RecalculateAllAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal(FreshOutput, await LiveOutputAsync(doc));
    }

    /// <summary>Сам інваріант — спільний для обох порядків.</summary>
    private async Task AssertSnapshotMatchesLiveAsync(TestDocument doc)
    {
        var (input, submitted) = await SubmittedAsync(doc);
        var live = await LiveOutputAsync(doc);
        var status = await ScalarAsync<byte>(
            $"SELECT Status FROM wf.ApprovalState WHERE DocumentId = {doc.DocumentId} " +
            $"AND SheetDefId = {doc.SheetDefId} AND PeriodKey = {doc.PeriodKey.Value}");

        Assert.Equal((byte)DocumentStatus.Submitted, status);

        // ⛔ Подана форма й жива обчислена комірка мусять казати одне й те саме.
        Assert.True(
            submitted == live,
            $"Зріз подання каже OUT = {submitted}, а жива обчислена комірка поданого аркуша — {live}: " +
            "перерахунок переписав подане число повз зріз.");

        // ⚠ І сам зріз узгоджений із формулою: подане OUT пораховане з поданого IN.
        Assert.True(
            submitted == input * 2,
            $"Зріз подання: IN = {input}, OUT = {submitted} — обчислене число не відповідає поданому входу.");
    }

    /// <summary>Вхід і обчислене значення рядка з останнього зрізу подання.</summary>
    private async Task<(decimal? Input, decimal? Output)> SubmittedAsync(TestDocument doc)
    {
        var row = doc.RowIds[0];

        var payload = await ScalarAsync<string>(
            $"SELECT TOP (1) PayloadJson FROM calc.SubmissionSnapshot WHERE DocumentId = {doc.DocumentId} " +
            $"AND SheetDefId = {doc.SheetDefId} AND PeriodKey = {doc.PeriodKey.Value} ORDER BY Id DESC");
        var cells = SubmissionPayload.Read(payload).ToList();

        decimal? ValueOf(int column) => cells
            .Where(c => c.Row == row && c.Column == column && c.Value is not null)
            .Select(c => (decimal?)decimal.Parse(c.Value!, System.Globalization.CultureInfo.InvariantCulture))
            .SingleOrDefault();

        return (ValueOf(doc.ColumnDefIds[1]), ValueOf(doc.ColumnDefIds[2]));
    }

    private async Task<decimal?> LiveOutputAsync(TestDocument doc)
        => await ScalarOrNullAsync<decimal?>(
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[2]}");

    /// <summary>Документ з одним рядком: IN = 21 (введене), OUT = 20 (обчислене, застаріле).</summary>
    private async Task<TestDocument> ArrangeAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 3, rowCount: 1, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        var row = doc.RowIds[0];
        var key = doc.PeriodKey.Value;
        var input = System.Globalization.CultureInfo.InvariantCulture;

        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({key}, {row}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, {InputValue.ToString(input)}, 0, 0), " +
            $"({key}, {row}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, {StaleOutput.ToString(input)}, 1, 0);");

        return doc;
    }

    /// <summary>Перерахунок на реальних сховищах; <paramref name="beforeWrite"/> — точка перемикання.</summary>
    private static RecalculationService BuildRecalculation(EcrDbContext db, TestDocument doc, Func<Task>? beforeWrite)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 2, DateTimeKind.Utc));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        // ⚠ Точка перемикання: `RunAsync` кличе це ПІСЛЯ обчислення і ДО
        // транзакції запису.
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(async _ =>
               {
                   if (beforeWrite is not null)
                   {
                       await beforeWrite().ConfigureAwait(false);
                   }

                   return (PeriodState?)PeriodState.Open;
               });

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns([]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), periods, Metadata(doc), versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    /// <summary>Подання на реальних сховищах; <paramref name="insideTransaction"/> — точка перемикання.</summary>
    private static SubmitSheetHandler BuildSubmit(EcrDbContext db, TestDocument doc, Func<Task>? insideTransaction)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.ProjectId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        // ⚠ Точка перемикання: подання кличе це ВСЕРЕДИНІ транзакції, ПІСЛЯ
        // того, як прочитало комірки в зріз, і ДО коміту.
        access.CurrentApprovalStepAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(async _ =>
              {
                  if (insideTransaction is not null)
                  {
                      await insideTransaction().ConfigureAwait(false);
                  }

                  return (ApprovalStepView?)null;
              });

        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());
        headers.GetValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, DocumentHeaderValueData>());

        // F-05: предмет цього класу — гонитва перерахунку формул проти
        // подання, не свіжість методологій; прогону розрахунку тут немає,
        // тож `IsStale` завжди `false`.
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetCalculationFreshnessAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new CalculationFreshness(null, null));

        return new SubmitSheetHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            Metadata(doc), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            headers,
            new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), User(), clock, new SheetEditGate(db),

            // ⛔ ТОЙ САМИЙ контекст, що й у подання: перерахунок має йти в його
            // транзакції, під його винятковим блокуванням — як у DI-скоупі.
            BuildRecalculation(db, doc, beforeWrite: null),
            methodologies);
    }

    /// <summary>
    /// Знімок структури з РЕАЛЬНИМИ ідентифікаторами аркуша, таблиці й колонок і
    /// однією формулою <c>OUT = [IN] * 2</c>: і подання, і перерахунок говорять про
    /// той самий аркуш.
    /// </summary>
    private static IMetadataCache Metadata(TestDocument doc)
    {
        var text = ColumnDefFor(doc, 1, "TXT", CellDataType.String);
        var input = ColumnDefFor(doc, 2, "IN", CellDataType.Decimal);
        var output = ColumnDefFor(doc, 3, "OUT", CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(text);
        table.AddColumn(input);
        table.AddColumn(output);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, "[IN] * 2", ExpressionDialect.Template);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(formula, FormulaId);
        formula.AssignColumn(output.Id);
        table.AddFormula(formula);

        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>
            {
                [text.Id] = text,
                [input.Id] = input,
                [output.Id] = output,
            },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int ordinal, string code, CellDataType type)
    {
        var id = doc.ColumnDefIds[ordinal - 1];
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<T?> ScalarOrNullAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
