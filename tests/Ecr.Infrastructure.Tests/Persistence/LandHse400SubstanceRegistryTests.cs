// tests/Ecr.Infrastructure.Tests/Persistence/LandHse400SubstanceRegistryTests.cs
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// LP-10: склад газу HSE400 ПО РЕЧОВИНІ на РЕАЛЬНОМУ SQL Server. Береться ТОЙ САМИЙ текст фрагмента
/// <c>CompositionMass</c>, що згенеровано в моделі Land (<c>argumentBridges.hse400.compositionMass</c>, C11_01);
/// довідники створюються з тими самими полями й ключами, а знімок вантажить бойовий
/// <see cref="RegistrySnapshotLoader"/>. Числа синтетичні.
/// </summary>
/// <remarks>
/// Доводиться: (1) та сама пара «рядок, потік, місяць» дає різні показники для різних речовин проходу;
/// (2) код поза словником і код без запису складу - <c>#N/A</c>; (3) довідник речовин БЕЗ первинного ключа
/// (<c>LAND_AIR_SUBSTANCE</c>: код запису <c>E333</c>, <c>SUBSTANCE_CODE</c> = 333) за номером не знаходиться, а
/// словник показників із первинним ключем SUBSTANCE_CODE - знаходиться.
/// Мутаційний доказ (лише цей клас): у фікстурі прибрати первинний ключ словника показників -
/// червоніє <see cref="Речовини_проходу_дають_різні_показники_а_невідома_й_без_запису_NA"/> (REGFIND шукатиме за кодом
/// запису «C333», а не за «333»).
/// </remarks>
[Collection("SqlServer")]
public sealed class LandHse400SubstanceRegistryTests(SqlServerFixture sql)
{
    private const string FuelGasInternal = "Fuel Gas (Internal) - Топливный газ (Внутренний)";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "LP-10")]
    public async Task Речовини_проходу_дають_різні_показники_а_невідома_й_без_запису_NA()
    {
        var f = await ArrangeAsync();
        var snapshot = await LoadAsync(f);

        Assert.Equal(0.02m, Eval(f, snapshot, f.Fragment, "333").AsNumber());
        Assert.Equal(0.0007m, Eval(f, snapshot, f.Fragment, "1728").AsNumber());

        // 1716 немає в словнику старої системи; 334 у словнику, але запису складу для потоку немає.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, f.Fragment, "1716").ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, f.Fragment, "334").ErrorCode);

        // Інший місяць: запису немає - рядок без результатів, а не значення січня.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, f.Fragment, "333", month: 2).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "LP-10")]
    public async Task Код_запису_довідника_речовин_не_є_SUBSTANCE_CODE()
    {
        var f = await ArrangeAsync();
        var snapshot = await LoadAsync(f);

        Assert.Equal(
            ExpressionErrors.NotAvailable,
            Eval(f, snapshot, $"REGFIND('{f.AirCode}', '333')", "333").ErrorCode);
        Assert.False(Eval(f, snapshot, $"REGFIND('{f.AirCode}', 'E333')", "333").IsError);
        Assert.Equal(
            "H2S_mass",
            Eval(f, snapshot, $"REGFIELD(REGFIND('{f.SubstanceCode}', '333'), 'INDICATOR')", "333").Value);
    }

    private static ExpressionValue Eval(Fixture f, IRegistrySnapshot snapshot, string expression, string substance, int month = 1)
    {
        var context = new TestEvaluationContext
        {
            CurrentSubstanceCode = substance,
            Period = new PeriodContext(new DateOnly(2026, month, 1), new DateOnly(2026, month, 28), CalendarMode.Actual, 2026, (byte)month),
        };
        context.Arguments["Land_TypeFuelOrStream"] = ExpressionValue.Text(FuelGasInternal);
        return Expr.Eval(f.Retarget(expression), new SnapshotContext(context, snapshot), ExpressionDialect.Methodology);
    }

    private static string ModelFragment()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Ecr.sln")))
        {
            root = root.Parent;
        }

        var path = Path.Combine(
            root?.FullName ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору."),
            "docs", "delivery", "reference-data", "land-template", "land-template.model.json");
        using var model = JsonDocument.Parse(File.ReadAllText(path));
        return model.RootElement.GetProperty("argumentBridges").GetProperty("hse400").GetProperty("compositionMass")
            .GetProperty("methodologies").GetProperty("ECW_C11_01").GetProperty("fragment").GetString()!;
    }

    private async Task<IRegistrySnapshot> LoadAsync(Fixture f)
    {
        await using var db = sql.CreateContext();
        return await new RegistrySnapshotLoader(db).LoadAsync(
            [f.StreamId, f.CompositionId, f.SubstanceId, f.AirId], new DateOnly(2026, 1, 31), null, CancellationToken.None);
    }

    private sealed record Fixture(string Tag, int StreamId, int CompositionId, int SubstanceId, int AirId)
    {
        public string StreamCode => $"LAND_HSE400_STREAM_{Tag}";

        public string CompositionCode => $"LAND_HSE400_COMPOSITION_{Tag}";

        public string SubstanceCode => $"LAND_HSE400_SUBSTANCE_{Tag}";

        public string AirCode => $"LAND_AIR_SUBSTANCE_{Tag}";

        public string Fragment => ModelFragment();

        /// <summary>Коди довідників у тексті - на тестові (у БД вони з суфіксом прогону).</summary>
        public string Retarget(string expression)
            => expression
                .Replace("'LAND_HSE400_COMPOSITION'", $"'{CompositionCode}'", StringComparison.Ordinal)
                .Replace("'LAND_HSE400_STREAM'", $"'{StreamCode}'", StringComparison.Ordinal)
                .Replace("'LAND_HSE400_SUBSTANCE'", $"'{SubstanceCode}'", StringComparison.Ordinal);
    }

    private async Task<Fixture> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var stream = await AddRegistryAsync(db, $"LAND_HSE400_STREAM_{tag}");
        var text = await AddFieldAsync(db, stream, "TEXT", CellDataType.String, 1, required: true);
        var prefix = await AddFieldAsync(db, stream, "PREFIX", CellDataType.String, 2);
        await AddPrimaryKeyAsync(db, stream, text);

        var composition = await AddRegistryAsync(db, $"LAND_HSE400_COMPOSITION_{tag}");
        var year = await AddFieldAsync(db, composition, "YEAR", CellDataType.Int, 1, required: true);
        var month = await AddFieldAsync(db, composition, "MONTH", CellDataType.Int, 2, required: true);
        var streamField = await AddFieldAsync(db, composition, "STREAM", CellDataType.String, 3, required: true);
        var indicator = await AddFieldAsync(db, composition, "INDICATOR", CellDataType.String, 4, required: true);
        var value = await AddFieldAsync(db, composition, "VALUE", CellDataType.Decimal, 5);
        await AddPrimaryKeyAsync(db, composition, year, month, streamField, indicator);

        // Словник показників: код запису C<код>, первинний ключ - SUBSTANCE_CODE (як у моделі Land).
        var substance = await AddRegistryAsync(db, $"LAND_HSE400_SUBSTANCE_{tag}");
        var substanceCode = await AddFieldAsync(db, substance, "SUBSTANCE_CODE", CellDataType.String, 1, required: true);
        var substanceIndicator = await AddFieldAsync(db, substance, "INDICATOR", CellDataType.String, 2);
        await AddPrimaryKeyAsync(db, substance, substanceCode);

        // Довідник речовин імпорту: код запису E<код>, SUBSTANCE_CODE - поле, первинного ключа немає.
        var air = await AddRegistryAsync(db, $"LAND_AIR_SUBSTANCE_{tag}");
        var airCode = await AddFieldAsync(db, air, "SUBSTANCE_CODE", CellDataType.String, 1, required: true);

        var n = 0;
        var entry = await AddEntryAsync(db, stream.Id, "S_FGI", ++n);
        await SetAsync(db, entry, text.Id, CellDataType.String, FuelGasInternal);
        await SetAsync(db, entry, prefix.Id, CellDataType.String, "FG_Internal");

        async Task SubstanceAsync(string code, string indicatorName)
        {
            var e = await AddEntryAsync(db, substance.Id, "C" + code, ++n);
            await SetAsync(db, e, substanceCode.Id, CellDataType.String, code);
            await SetAsync(db, e, substanceIndicator.Id, CellDataType.String, indicatorName);
        }

        await SubstanceAsync("333", "H2S_mass");
        await SubstanceAsync("334", "CS2_mass");
        await SubstanceAsync("1728", "EMercaptan_mass");

        var air333 = await AddEntryAsync(db, air.Id, "E333", ++n);
        await SetAsync(db, air333, airCode.Id, CellDataType.String, "333");

        async Task CompositionAsync(int m, string i, decimal v)
        {
            var e = await AddEntryAsync(db, composition.Id, $"H2026{m:D2}_FGI_{i}", ++n);
            await SetAsync(db, e, year.Id, CellDataType.Int, 2026);
            await SetAsync(db, e, month.Id, CellDataType.Int, m);
            await SetAsync(db, e, streamField.Id, CellDataType.String, "FG_Internal");
            await SetAsync(db, e, indicator.Id, CellDataType.String, i);
            await SetAsync(db, e, value.Id, CellDataType.Decimal, v);
        }

        await CompositionAsync(1, "H2S_mass", 0.02m);
        await CompositionAsync(1, "EMercaptan_mass", 0.0007m);

        return new Fixture(tag, stream.Id, composition.Id, substance.Id, air.Id);
    }

    private static async Task<RegistryDef> AddRegistryAsync(EcrDbContext db, string code)
    {
        var registry = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry;
    }

    private static async Task<RegistryFieldDef> AddFieldAsync(
        EcrDbContext db, RegistryDef registry, string code, CellDataType dataType, int ordinal, bool required = false)
    {
        var field = new RegistryFieldDef(registry.Id, EcrCode.Create(code), Text(code), dataType, ordinal);
        field.Update(Text(code), ordinal, required);
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

    private static async Task<long> AddEntryAsync(EcrDbContext db, int registryDefId, string code, int ordinal)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code), 1, DateTime.UtcNow);
        entry.SetOrdinal(ordinal);
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

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Контекст із комірками тесту, символом речовини й бойовим знімком довідників.</summary>
    private sealed class SnapshotContext(IEvaluationContext inner, IRegistrySnapshot snapshot) : IEvaluationContext
    {
        public IRegistrySnapshot? Registries => snapshot;

        public PeriodContext Period => inner.Period;

        public string? CurrentSubstanceCode => inner.CurrentSubstanceCode;

        public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference) => inner.Read(reference);

        public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
            => inner.GetCell(tableDefId, rowKey, columnDefId, periodOffset);

        public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
            => inner.GetCellsByPredicate(tableDefId, filterJson, columnDefId);

        public ExpressionValue GetArgument(string name) => inner.GetArgument(name);

        public ExpressionValue GetConstant(string name) => inner.GetConstant(name);

        public ExpressionValue GetFormulaResult(string name) => inner.GetFormulaResult(name);

        public ExpressionValue GetHeader(string name) => inner.GetHeader(name);

        public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
            => inner.GetRegistryField(registryEntryId, fieldCode);

        public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
            => inner.Convert(value, fromUnitCode, toUnitCode);
    }
}
