// tests/Ecr.Infrastructure.Tests/Persistence/LandHse400CompositionRegistryTests.cs
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
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// LP-10: склад газу HSE400 для методологій Land на РЕАЛЬНОМУ SQL Server. Береться ТОЙ САМИЙ текст моста, який
/// згенеровано в моделі Land (<c>land-template.model.json</c>, колонка <c>Density20</c> таблиці T9_04 і Lookup-варіант
/// T10_01), довідники створюються з тим самим складом полів і складеним ключем (рік, місяць, потік, показник), а
/// знімок вантажить бойовий <see cref="RegistrySnapshotLoader"/>.
/// </summary>
/// <remarks>
/// Значень прод-даних тут немає: числа синтетичні. Перевіряється саме правило вибору:
/// <list type="bullet">
/// <item>потік береться з тексту Land-поля за словником (<c>FG_Internal</c> і <c>LPG_HMB</c> дають РІЗНІ числа);</item>
/// <item>період рядка (<c>[Period].Year</c>, <c>[Period].Sequence</c>) вибирає місяць;</item>
/// <item>немає запису за (рік, місяць, потік, показник) - <c>#N/A</c>: аргумент не обчислено, рядок без результатів
/// (стара система: <c>continue</c>); те саме для невідомого тексту палива;</item>
/// <item>Lookup-джерело (<c>Land_TypeFuel</c> у T10_01): текст - назва запису довідника палива.</item>
/// </list>
/// Мутаційні докази (лише цей клас): у моделі замінити <c>[Period].Sequence</c> на <c>[Period].Year</c> у мосту
/// T9_04.Density20 - червоніє <see cref="Потік_і_місяць_вибирають_різні_значення"/>; у словнику потоків міняти місцями
/// префікси - червоніють усі, що читають значення потоку.
/// </remarks>
[Collection("SqlServer")]
public sealed class LandHse400CompositionRegistryTests(SqlServerFixture sql)
{
    private const string FuelGasInternal = "Fuel gas (Internal) - Топливный газ (Внутренний)";
    private const string LpgHmb = "LPG (HMB) - СУГ (МТБ)";
    private static readonly DateOnly EndOfJanuary = new(2026, 1, 31);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "LP-10")]
    public async Task Потік_і_місяць_вибирають_різні_значення()
    {
        var f = await ArrangeAsync();
        var snapshot = await LoadAsync(f);

        // Той самий місяць - різні потоки - різні числа.
        Assert.Equal(0.7m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(1.9m, Eval(f, snapshot, ModelDensity20(), LpgHmb, month: 1).AsNumber());

        // Той самий потік - інший місяць - інше число.
        Assert.Equal(0.75m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 2).AsNumber());

        // Текст палива - без урахування регістру (ключ словника ignoreCase).
        Assert.Equal(0.7m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal.ToUpperInvariant(), month: 1).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "LP-10")]
    public async Task Немає_запису_за_місяць_чи_рік_чи_потік_чи_показник_дає_NA_а_не_нуль()
    {
        var f = await ArrangeAsync();
        var snapshot = await LoadAsync(f);

        // Лютий: LPG_HMB немає - рядок без результатів, а не 0 і не значення січня.
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, ModelDensity20(), LpgHmb, month: 2).ErrorCode);

        // Інший рік (стара система не мала складу за 2025).
        Assert.Equal(
            ExpressionErrors.NotAvailable,
            Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1, year: 2025).ErrorCode);

        // Показник, якого в цьому потоці немає (HMB не має MercaptanSulphurContent).
        Assert.Equal(
            ExpressionErrors.NotAvailable,
            Eval(f, snapshot, ModelExpression("T9_08", "MercaptanSulphurContent"), LpgHmb, month: 1).ErrorCode);

        // Текст палива, якому словник потоку не відповідає (Diesel).
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, ModelDensity20(), "Diesel - Дизель", month: 1).ErrorCode);

        // Текст не заповнено - null, а не помилка (як REGFIND з порожньою частиною ключа).
        Assert.True(Eval(f, snapshot, ModelDensity20(), null, month: 1).IsNull);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "LP-10")]
    public async Task Показник_за_іменем_аргументу_а_запасний_гексан_і_канонічні_імена_лежать_у_довіднику()
    {
        var f = await ArrangeAsync();
        var snapshot = await LoadAsync(f);

        Assert.Equal(1.234m, Eval(f, snapshot, ModelExpression("T9_04", "Vddg"), FuelGasInternal, month: 1).AsNumber());

        // C11_08: аргумент Density читає показник Density20, FuelLowerHeating - FuelLowerHeating20, H2Smass - H2S_mass.
        Assert.Equal(0.7m, Eval(f, snapshot, ModelExpression("T9_08", "Density"), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(34.5m, Eval(f, snapshot, ModelExpression("T9_08", "FuelLowerHeating"), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(0.02m, Eval(f, snapshot, ModelExpression("T9_08", "H2Smass"), FuelGasInternal, month: 1).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "LP-10")]
    public async Task Потік_за_Lookup_полем_палива_береться_за_назвою_запису_довідника()
    {
        var f = await ArrangeAsync();
        var snapshot = await LoadAsync(f);

        // T10_01 (C12_03): Land_TypeFuel - Lookup, значення комірки - id запису довідника палива.
        var expression = ModelExpression("T10_01", "Density20");
        Assert.Equal(1.9m, EvalLookup(f, snapshot, expression, f.FuelLpgHmb, month: 1).AsNumber());
        Assert.Equal(0.7m, EvalLookup(f, snapshot, expression, f.FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(ExpressionErrors.NotAvailable, EvalLookup(f, snapshot, expression, f.FuelDiesel, month: 1).ErrorCode);
    }

    private static string ModelExpression(string table, string column)
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
        foreach (var sheet in model.RootElement.GetProperty("sheets").EnumerateArray())
        {
            foreach (var t in sheet.GetProperty("tables").EnumerateArray())
            {
                if (t.GetProperty("code").GetString() != table)
                {
                    continue;
                }

                foreach (var c in t.GetProperty("columns").EnumerateArray())
                {
                    if (c.GetProperty("code").GetString() == column && c.TryGetProperty("expression", out var expression))
                    {
                        return expression.GetString()!;
                    }
                }
            }
        }

        throw new InvalidOperationException($"У моделі Land немає моста {table}.{column}.");
    }

    private static string ModelDensity20() => ModelExpression("T9_04", "Density20");

    private static ExpressionValue Eval(Fixture f, IRegistrySnapshot snapshot, string modelExpression, string? fuelText, int month, int year = 2026)
    {
        var context = new TestEvaluationContext
        {
            Period = new PeriodContext(new DateOnly(year, month, 1), new DateOnly(year, month, 28), CalendarMode.Actual, year, (byte)month),
        };
        context.SetCell("S", "T", string.Empty, "TypeOfFuel", fuelText is null ? ExpressionValue.Null : ExpressionValue.Text(fuelText));
        return Expr.Eval(f.Retarget(modelExpression), new SnapshotContext(context, snapshot));
    }

    private static ExpressionValue EvalLookup(Fixture f, IRegistrySnapshot snapshot, string modelExpression, long fuelEntryId, int month)
    {
        var context = new TestEvaluationContext
        {
            Period = new PeriodContext(new DateOnly(2026, month, 1), new DateOnly(2026, month, 28), CalendarMode.Actual, 2026, (byte)month),
        };
        context.SetCell("S", "T", string.Empty, "Land_TypeFuel", ExpressionValue.Number(fuelEntryId));
        return Expr.Eval(f.Retarget(modelExpression), new SnapshotContext(context, snapshot));
    }

    private async Task<IRegistrySnapshot> LoadAsync(Fixture f)
    {
        await using var db = sql.CreateContext();
        return await new RegistrySnapshotLoader(db).LoadAsync(
            [f.StreamId, f.CompositionId, f.FuelId], EndOfJanuary, null, CancellationToken.None);
    }

    private sealed record Fixture(
        string Tag, int StreamId, int CompositionId, int FuelId, long FuelLpgHmb, long FuelGasInternal, long FuelDiesel)
    {
        public string StreamCode => $"LAND_HSE400_STREAM_{Tag}";

        public string CompositionCode => $"LAND_HSE400_COMPOSITION_{Tag}";

        public string FuelCode => $"LAND_FUEL_{Tag}";

        /// <summary>Коди довідників у тексті моста - на тестові (у БД вони з суфіксом прогону).</summary>
        public string Retarget(string expression)
            => expression
                .Replace("'LAND_HSE400_COMPOSITION'", $"'{CompositionCode}'", StringComparison.Ordinal)
                .Replace("'LAND_HSE400_STREAM'", $"'{StreamCode}'", StringComparison.Ordinal);
    }

    private async Task<Fixture> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        // Словник потоків: TEXT (первинний ключ, без регістру) -> PREFIX.
        var stream = await AddRegistryAsync(db, $"LAND_HSE400_STREAM_{tag}");
        var text = await AddFieldAsync(db, stream, "TEXT", CellDataType.String, 1, required: true);
        var prefix = await AddFieldAsync(db, stream, "PREFIX", CellDataType.String, 2);
        await AddPrimaryKeyAsync(db, stream, text);

        // Склад: (YEAR, MONTH, STREAM, INDICATOR) -> VALUE - рівно як у моделі Land.
        var composition = await AddRegistryAsync(db, $"LAND_HSE400_COMPOSITION_{tag}");
        var year = await AddFieldAsync(db, composition, "YEAR", CellDataType.Int, 1, required: true);
        var month = await AddFieldAsync(db, composition, "MONTH", CellDataType.Int, 2, required: true);
        var streamField = await AddFieldAsync(db, composition, "STREAM", CellDataType.String, 3, required: true);
        var indicator = await AddFieldAsync(db, composition, "INDICATOR", CellDataType.String, 4, required: true);
        var value = await AddFieldAsync(db, composition, "VALUE", CellDataType.Decimal, 5);
        await AddPrimaryKeyAsync(db, composition, year, month, streamField, indicator);

        // Довідник палива шаблону (Lookup-колонка Land_TypeFuel): NAME.
        var fuel = await AddRegistryAsync(db, $"LAND_FUEL_{tag}");
        var name = await AddFieldAsync(db, fuel, "NAME", CellDataType.String, 1, required: true);

        async Task StreamAsync(string code, string textValue, string prefixValue)
        {
            var entry = await AddEntryAsync(db, stream.Id, code, ordinal: 1);
            await SetAsync(db, entry, text.Id, CellDataType.String, textValue);
            await SetAsync(db, entry, prefix.Id, CellDataType.String, prefixValue);
        }

        await StreamAsync("S_FGI", FuelGasInternal, "FG_Internal");
        await StreamAsync("S_LPGH", LpgHmb, "LPG_HMB");

        var n = 0;
        async Task CompositionAsync(int y, int m, string s, string i, decimal v)
        {
            var entry = await AddEntryAsync(db, composition.Id, $"H{y}{m:D2}_{s}_{i}", ordinal: ++n);
            await SetAsync(db, entry, year.Id, CellDataType.Int, y);
            await SetAsync(db, entry, month.Id, CellDataType.Int, m);
            await SetAsync(db, entry, streamField.Id, CellDataType.String, s);
            await SetAsync(db, entry, indicator.Id, CellDataType.String, i);
            await SetAsync(db, entry, value.Id, CellDataType.Decimal, v);
        }

        await CompositionAsync(2026, 1, "FG_Internal", "Density20", 0.7m);
        await CompositionAsync(2026, 1, "LPG_HMB", "Density20", 1.9m);
        await CompositionAsync(2026, 2, "FG_Internal", "Density20", 0.75m);
        await CompositionAsync(2026, 1, "FG_Internal", "Vddg", 1.234m);
        await CompositionAsync(2026, 1, "FG_Internal", "FuelLowerHeating20", 34.5m);
        await CompositionAsync(2026, 1, "FG_Internal", "H2S_mass", 0.02m);

        async Task<long> FuelAsync(string code, string title)
        {
            var entry = await AddEntryAsync(db, fuel.Id, code, ordinal: 1);
            await SetAsync(db, entry, name.Id, CellDataType.String, title);
            return entry;
        }

        var fuelLpg = await FuelAsync("LPGHMB", LpgHmb);
        var fuelInternal = await FuelAsync("FGINTERNAL", FuelGasInternal);
        var fuelDiesel = await FuelAsync("DIESEL", "Diesel - Дизель");

        return new Fixture(tag, stream.Id, composition.Id, fuel.Id, fuelLpg, fuelInternal, fuelDiesel);
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

    /// <summary>Контекст із комірками тесту й бойовим знімком довідників.</summary>
    private sealed class SnapshotContext(IEvaluationContext inner, IRegistrySnapshot snapshot) : IEvaluationContext
    {
        public IRegistrySnapshot? Registries => snapshot;

        public PeriodContext Period => inner.Period;

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
