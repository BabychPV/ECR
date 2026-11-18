// tests/Ecr.Infrastructure.Tests/Persistence/LandHse400CompositionRegistryTests.SourceGate.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B80 (N1, A2): ворота зв'язки <c>cfg.DocumentSourceBinding</c> у <see cref="Hse400CompositionSnapshotLoader"/> —
/// за <see cref="DocumentSourceState.ApprovedOnly"/> Land отримує склад лише затверджених місяців HSE400 (варіант (a):
/// інший місяць без даних), за <see cref="DocumentSourceState.Any"/> — як до B80. Справжній SQL Server.
/// </summary>
/// <remarks>
/// ⚠ Правило 394: кожен тест має власні коди довідника й шаблону (фікстура), тож його зв'язка не зачіпає
/// робочу зв'язку HSE400 → LAND_HSE400_COMPOSITION інших тестів.
/// </remarks>
public sealed partial class LandHse400CompositionRegistryTests
{
    private static readonly int[] MonthOne = [1];
    private static readonly int[] MonthSeven = [7];
    private static readonly int[] MonthThree = [3];
    private static readonly int[] SecondQuarter = [4, 5, 6];

    /// <remarks>
    /// Мутації (B80-M1/M2): прибрати перевірку воріт у циклі вводу — лютий дає 0.81; у циклі F_ — лютий F_ дає 41.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task ApprovedOnly_дає_лише_затверджений_місяць_а_незатверджений_без_даних_і_без_довідника()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(
            ("FG_Internal", "Density_20", 1, 0.81m),
            ("FG_Internal", "Density_20", 2, 0.82m),
            ("FG_Internal", "F_FuelLowerHeating_20", 1, 40.5m),
            ("FG_Internal", "F_FuelLowerHeating_20", 2, 41m));
        var doc = lastBuilt!;
        await AddBindingAsync(f, doc.TemplateId, DocumentSourceState.ApprovedOnly);

        // Січень затверджено; лютий лише подано — «не затверджено».
        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202601, DocumentStatus.Approved);
        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202602, DocumentStatus.Submitted);

        var snapshot = await LoadFromDocumentAsync(f, template);
        var heating = ModelExpression("T9_08", "FuelLowerHeating");

        Assert.Equal(0.81m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(40.5m, Eval(f, snapshot, heating, FuelGasInternal, month: 1).AsNumber());

        // Лютий: ні вводу, ні F_ документа — і НЕ рядок довідника (0.75): рік документа довідник не повертає (G2).
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 2).ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, heating, FuelGasInternal, month: 2).ErrorCode);
    }

    /// <remarks>
    /// Мутація (B80-M3, умовчання Any): <c>approvedOnly = binding?.SourceState == ApprovedOnly</c> → <c>true</c> —
    /// зв'язка на Any без жодного затвердження дає #N/A замість 0.81 і 0.82.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Any_читає_документ_як_до_B80_без_жодного_затвердження()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(
            ("FG_Internal", "Density_20", 1, 0.81m),
            ("FG_Internal", "Density_20", 2, 0.82m));
        await AddBindingAsync(f, lastBuilt!.TemplateId, DocumentSourceState.Any);

        var snapshot = await LoadFromDocumentAsync(f, template);

        Assert.Equal(0.81m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(0.82m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 2).AsNumber());
    }

    /// <summary>
    /// ✎ F13 G3 (AU, batch-1118-2): затверджена КОПІЯ аркуша (Contract 2, …) не робить місяць джерела затвердженим —
    /// ворота дивляться лише на оригінал аркуша. Січень: копія Approved, оригінал лише поданий → у Land січня немає даних.
    /// </summary>
    /// <remarks>Мутація (G3): у <c>DocumentSourceGate.ApprovedMonthsAsync</c> прибрати <c>s.SheetCopyId == null</c> —
    /// копія зараховує січень, ворота пропускають 0.81, тест червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task ApprovedOnly_затверджена_копія_аркуша_не_робить_місяць_затвердженим()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(("FG_Internal", "Density_20", 1, 0.81m));
        var doc = lastBuilt!;
        await AddBindingAsync(f, doc.TemplateId, DocumentSourceState.ApprovedOnly);

        long copyId;
        await using (var db = sql.CreateContext())
        {
            var original = await db.DocumentSheets
                .FirstOrDefaultAsync(s => s.DocumentId == doc.DocumentId && s.SheetDefId == doc.SheetDefId && s.CopyOfId == null);
            if (original is null)
            {
                original = new Ecr.Domain.Entities.Documents.DocumentSheet(doc.DocumentId, doc.SheetDefId);
                db.DocumentSheets.Add(original);
                await db.SaveChangesAsync();
            }

            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync();
            copyId = copy.Id;
        }

        // Оригінал — лише поданий; копія того самого аркуша й місяця — затверджена.
        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202601, DocumentStatus.Submitted);
        await using (var db = sql.CreateContext())
        {
            var copyState = new ApprovalState(doc.DocumentId, doc.SheetDefId, 202601, copyId);
            copyState.Submit(userId: 5, Stamp);
            copyState.Approve(userId: 6, Stamp);
            db.ApprovalStates.Add(copyState);
            await db.SaveChangesAsync();
        }

        await using (var db = sql.CreateContext())
        {
            var tableIds = await db.TableDefs.Where(t => t.SheetDefId == doc.SheetDefId).Select(t => t.Id).ToListAsync();
            Assert.Empty(await DocumentSourceGate.ApprovedMonthsAsync(db, [doc.DocumentId], tableIds, CancellationToken.None));
        }

        var snapshot = await LoadFromDocumentAsync(f, template);
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Повернутий_у_роботу_місяць_і_нульовий_паритет_незатвердженого_місяця_не_дають_даних()
    {
        var f = await ArrangeAsync();

        // Січень: увесь ввід — нулі, F_ без результату (паритет дав би 0). Затверджено, потім повернуто в роботу.
        var template = await BuildDocumentAsync(
            [("FG_Internal", "F_FuelLowerHeating_20")],
            ("FG_Internal", "Density_20", 1, 0m),
            ("FG_Internal", "Vddg", 1, 0m));
        var doc = lastBuilt!;
        await AddBindingAsync(f, doc.TemplateId, DocumentSourceState.ApprovedOnly);
        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202601, DocumentStatus.Approved, reopen: true);

        var snapshot = await LoadFromDocumentAsync(f, template);

        Assert.Equal(
            ExpressionErrors.NotAvailable,
            Eval(f, snapshot, ModelExpression("T9_08", "FuelLowerHeating"), FuelGasInternal, month: 1).ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).ErrorCode);
    }

    /// <remarks>
    /// Мутація (B80-M4, перекриття): <c>enabled ?? binding?.ReadFromDocument ?? false</c> → <c>binding?.ReadFromDocument ?? false</c>
    /// — явне <c>false</c> більше не вимикає зв'язку з бітом 1.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Явний_ключ_перекриває_біт_зв_язки_а_без_ключа_вирішує_біт()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(("FG_Internal", "Density_20", 1, 0.81m));
        await AddBindingAsync(f, lastBuilt!.TemplateId, DocumentSourceState.Any, readFromDocument: true);

        // Ключ не задано — біт 1: документ.
        Assert.Equal(0.81m, Eval(f, await LoadFromDocumentAsync(f, template, enabled: null), ModelDensity20(), FuelGasInternal, month: 1).AsNumber());

        // Явне false перекриває біт 1: довідник.
        Assert.Equal(0.7m, Eval(f, await LoadFromDocumentAsync(f, template, enabled: false), ModelDensity20(), FuelGasInternal, month: 1).AsNumber());

        // Біт 0 без ключа — довідник; явне true перекриває біт 0 — документ.
        await using (var db = sql.CreateContext())
        {
            await db.DocumentSourceBindings.Where(b => b.RegistryDefId == f.CompositionId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReadFromDocument, false));
        }

        Assert.Equal(0.7m, Eval(f, await LoadFromDocumentAsync(f, template, enabled: null), ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(0.81m, Eval(f, await LoadFromDocumentAsync(f, template, enabled: true), ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Без_зв_язки_і_без_ключа_читається_довідник_як_до_B80()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(("FG_Internal", "Density_20", 1, 0.81m));

        var snapshot = await LoadFromDocumentAsync(f, template, enabled: null);

        Assert.Equal(0.7m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
    }

    /// <remarks>Мутація (B80-M5, період-власник): у <c>OwnedMonths</c> річний період = 1 місяць — тест червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Період_володіє_місяцями_за_періодичністю_проєкту()
    {
        Assert.Equal(MonthOne, DocumentSourceGate.OwnedMonths(PeriodKind.Monthly, 1));
        Assert.Equal(MonthSeven, DocumentSourceGate.OwnedMonths(PeriodKind.Monthly, 7));
        Assert.Equal(Enumerable.Range(1, 12), DocumentSourceGate.OwnedMonths(PeriodKind.Yearly, 1));
        Assert.Equal(SecondQuarter, DocumentSourceGate.OwnedMonths(PeriodKind.Quarterly, 4));
        Assert.Equal(MonthThree, DocumentSourceGate.OwnedMonths(PeriodKind.Custom, 3));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Річний_якір_затверджено_проходять_усі_місяці_року()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(
            ("FG_Internal", "Density_20", 1, 0.81m),
            ("FG_Internal", "Density_20", 2, 0.82m));
        var doc = lastBuilt!;
        await using (var db = sql.CreateContext())
        {
            await db.Projects.Where(p => p.Id == doc.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, PeriodKind.Yearly));
        }

        await AddBindingAsync(f, doc.TemplateId, DocumentSourceState.ApprovedOnly);
        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202601, DocumentStatus.Approved);

        var snapshot = await LoadFromDocumentAsync(f, template);

        Assert.Equal(0.81m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(0.82m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 2).AsNumber());
    }

    /// <summary>
    /// Оновлення наявного стенда: секція сіду <c>B80:source-binding</c> (той самий текст, що виконує застосунок, із
    /// кодами тесту замість робочих) створює зв'язку на <c>Any</c>, а повторний старт НЕ повертає перемкнутий стан.
    /// </summary>
    /// <remarks>
    /// Мутації (B80-M11/M12): у сіді <c>SourceState</c> 0 → 1 — новий рядок не Any, червоний; MERGE із
    /// <c>WHEN MATCHED THEN UPDATE SET SourceState = 0</c> — повторний сід скидає ApprovedOnly, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Сід_заводить_зв_язку_на_Any_і_не_повертає_перемкнутий_стан()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(("FG_Internal", "Density_20", 1, 0.81m));
        var consumer = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: Year2026);
        string consumerCode;
        await using (var db = sql.CreateContext())
        {
            consumerCode = await db.Templates.Where(t => t.Id == consumer.TemplateId).Select(t => t.Code).SingleAsync();
        }

        var bindingCode = $"B80SEED_{f.Tag}";
        var section = SeedSection()
            .Replace("N'HSE400_LAND_COMPOSITION'", $"N'{bindingCode}'", StringComparison.Ordinal)
            .Replace("N'LAND_HSE400_COMPOSITION'", $"N'{f.CompositionCode}'", StringComparison.Ordinal)
            .Replace("N'HSE400'", $"N'{template}'", StringComparison.Ordinal)
            .Replace("N'LAND_UI'", $"N'{consumerCode}'", StringComparison.Ordinal);

        await RunBatchesAsync(section);

        await using (var db = sql.CreateContext())
        {
            var binding = await db.DocumentSourceBindings.AsNoTracking().Include(b => b.Consumers).SingleAsync(b => b.Code == bindingCode);
            Assert.Equal(DocumentSourceState.Any, binding.SourceState);
            Assert.Equal(DocumentSourcePolicy.ConfirmWithReason, binding.Policy);
            Assert.True(binding.ReadFromDocument);
            Assert.True(binding.RecalculateDependents);
            Assert.Equal(consumer.TemplateId, Assert.Single(binding.Consumers).TemplateId);

            await db.DocumentSourceBindings.Where(b => b.Code == bindingCode)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, DocumentSourceState.ApprovedOnly));
        }

        // Повторний старт (сід на кожному старті) — рядок той самий, стан не скинуто, споживач не задвоєно.
        await RunBatchesAsync(section);

        await using (var db = sql.CreateContext())
        {
            var binding = await db.DocumentSourceBindings.AsNoTracking().Include(b => b.Consumers).SingleAsync(b => b.Code == bindingCode);
            Assert.Equal(DocumentSourceState.ApprovedOnly, binding.SourceState);
            Assert.Single(binding.Consumers);
        }
    }

    private static string SeedSection()
    {
        using var stream = typeof(SeedScript).Assembly.GetManifestResourceStream(SeedScript.ResourcePrefix + SeedScript.EpilogueFile)
                           ?? throw new InvalidOperationException("epilogue resource missing");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var start = text.IndexOf("-- B80:source-binding ── зв'язка", StringComparison.Ordinal);
        var end = text.IndexOf("-- B80:source-binding ── кінець секції", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "секції B80:source-binding у 99-epilogue.sql немає");
        return text[start..end];
    }

    private async Task RunBatchesAsync(string script)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        foreach (var batch in System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task AddBindingAsync(Fixture f, int templateId, DocumentSourceState state, bool readFromDocument = true)
    {
        await using var db = sql.CreateContext();
        var binding = new DocumentSourceBinding($"B80T_{f.Tag}", templateId, f.CompositionId);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();
        await db.DocumentSourceBindings.Where(b => b.Id == binding.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.SourceState, state)
                .SetProperty(b => b.ReadFromDocument, readFromDocument));
    }

    private async Task AddApprovalAsync(
        long documentId, int sheetDefId, int periodKey, DocumentStatus status, bool reopen = false)
    {
        await using var db = sql.CreateContext();
        var state = new ApprovalState(documentId, sheetDefId, periodKey);
        state.Submit(userId: 5, Stamp);
        if (status == DocumentStatus.Approved)
        {
            state.Approve(userId: 6, Stamp);
        }

        if (reopen)
        {
            state.Reopen(userId: 6, "B80 test: correction", Stamp);
        }

        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }
}
