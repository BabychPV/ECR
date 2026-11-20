using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.MethodologyScriptMigration.Cli;
using Microsoft.EntityFrameworkCore;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>Ідентифікатори засіяних сутностей.</summary>
internal sealed record SeedResult(
    int LibraryVersionId,
    int MainV1Id,
    int MainV2Id,
    int DraftId,
    int ModuleVersionId,
    int ScriptVersionId,
    int BrokenVersionId,
    long GasEntryId,
    long RunningEntryId);

/// <summary>
/// Засів невеликого, але повного набору для міграційного інструмента: бібліотека з константами за категорією, методологія
/// з імпортом, константами за речовиною й датою, правилами (запис довідника й текст), фільтром активності, двома
/// речовинами, ще одна версія без правил (невдача), чернетка, версія рівня Module і версія, що вже на Script.
/// Коди фіксовані: база тестового прогону створюється з нуля, засів іде один раз.
/// </summary>
internal static class MigrationSeed
{
    public const string Library = "MsLib";
    public const string Main = "MsMain";
    public const string Draft = "MsDraft";
    public const string Module = "MsModule";
    public const string Scripted = "MsScripted";
    public const string Broken = "MsBroken";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, SeedResult> Done = new(StringComparer.Ordinal);
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    public static async Task<SeedResult> EnsureAsync(string connectionString)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Done.TryGetValue(connectionString, out var result))
            {
                result = await SeedAsync(connectionString).ConfigureAwait(false);
                Done[connectionString] = result;
            }

            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<SeedResult> SeedAsync(string connectionString)
    {
        await using var db = MigrationCli.CreateContext(connectionString, readOnly: false);

        // ---- Довідники ----
        var status = new RegistryDef(EcrCode.Create("MS_STATUS"), Name("status"), false);
        var fuel = new RegistryDef(EcrCode.Create("MS_FUEL"), Name("fuel"), false);
        var substance = new RegistryDef(EcrCode.Create("MS_SUBST"), Name("substance"), false);
        db.RegistryDefs.AddRange(status, fuel, substance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var running = new RegistryEntry(status.Id, EcrCode.Create("RUNNING"), Name("Running"));
        var stopped = new RegistryEntry(status.Id, EcrCode.Create("STOPPED"), Name("Stopped"));
        var gas = new RegistryEntry(fuel.Id, EcrCode.Create("GAS"), Name("Gas"));
        var e301 = new RegistryEntry(substance.Id, EcrCode.Create("E301"), Name("NO2"));
        var e304 = new RegistryEntry(substance.Id, EcrCode.Create("E304"), Name("NO"));
        db.RegistryEntries.AddRange(running, stopped, gas, e301, e304);

        // Код речовини живе в полі SUBSTANCE_CODE (код запису E301 не може бути числом).
        var codeField = new RegistryFieldDef(substance.Id, EcrCode.Create("SUBSTANCE_CODE"), Name("code"), CellDataType.String, 1);
        db.RegistryFieldDefs.Add(codeField);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var v301 = new RegistryValue(e301.Id, codeField.Id);
        v301.Set(CellDataType.String, "0301", null);
        var v304 = new RegistryValue(e304.Id, codeField.Id);
        v304.Set(CellDataType.String, "0304", null);
        db.RegistryValues.AddRange(v301, v304);

        // ---- Шаблон: дві таблиці ----
        var template = new Template(EcrCode.Create("MS_TPL"), Name("template"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var templateVersion = new TemplateVersion(template.Id, "1.0", 1, Now);
        db.TemplateVersions.Add(templateVersion);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var sheet = new SheetDef(templateVersion.Id, EcrCode.Create("MS_SHEET"), Name("sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table1 = new TableDef(
            sheet.Id, EcrCode.Create("MS_T1"), Name("t1"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        var table2 = new TableDef(
            sheet.Id, EcrCode.Create("MS_T2"), Name("t2"), 2, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.AddRange(table1, table2);
        await db.SaveChangesAsync().ConfigureAwait(false);

        ColumnDef Column(TableDef table, string code, int ordinal, CellDataType type, RegistryDef? lookup = null)
        {
            var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), ordinal, type);
            if (lookup is not null)
            {
                column.SetLookup(lookup.Id);
            }

            db.ColumnDefs.Add(column);
            return column;
        }

        var fuelColumn = Column(table1, "Fuel", 1, CellDataType.Lookup, fuel);
        var zoneColumn = Column(table1, "Zone", 2, CellDataType.String);
        Column(table1, "State", 3, CellDataType.Lookup, status);
        var qtyColumn = Column(table1, "Qty", 4, CellDataType.Decimal);
        Column(table2, "Zone", 1, CellDataType.String);
        var qty2Column = Column(table2, "Qty", 2, CellDataType.Decimal);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var unitT = (await db.Units.FirstAsync(u => u.Code == "t").ConfigureAwait(false)).Id;
        var unitKg = (await db.Units.FirstAsync(u => u.Code == "kg").ConfigureAwait(false)).Id;

        // ---- Методології ----
        var library = new Methodology(EcrCode.Create(Library), Name("library"));
        library.SetKind(MethodologyKind.Library);
        var main = new Methodology(EcrCode.Create(Main), Name("main"));
        main.SetRowActivityFilter("MS_STATUS:RUNNING");
        var draft = new Methodology(EcrCode.Create(Draft), Name("draft"));
        var module = new Methodology(EcrCode.Create(Module), Name("module"));
        var scripted = new Methodology(EcrCode.Create(Scripted), Name("scripted"));
        var broken = new Methodology(EcrCode.Create(Broken), Name("broken"));
        db.Methodologies.AddRange(library, main, draft, module, scripted, broken);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var libVersion = new MethodologyVersion(library.Id, "V1", CalculationLevel.Configuration, 1, Now);
        var mainV1 = new MethodologyVersion(main.Id, "V1", CalculationLevel.Configuration, 1, Now);
        var mainV2 = new MethodologyVersion(main.Id, "V2", CalculationLevel.Configuration, 1, Now);
        var draftVersion = new MethodologyVersion(draft.Id, "D1", CalculationLevel.Configuration, 1, Now);
        var moduleVersion = new MethodologyVersion(module.Id, "M1", CalculationLevel.Module, 1, Now);
        var scriptedVersion = new MethodologyVersion(scripted.Id, "S1", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
        var brokenVersion = new MethodologyVersion(broken.Id, "B1", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.AddRange(libVersion, mainV1, mainV2, draftVersion, moduleVersion, scriptedVersion, brokenVersion);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ---- Бібліотека: формула й константа K за категорією ----
        var dens = new MethodologyFormula(libVersion.Id, EcrCode.Create("Dens"), "CST.K * 2");
        dens.SetEvaluationOrder(1);
        db.MethodologyFormulas.Add(dens);
        db.MethodologyCategories.AddRange(
            new MethodologyCategory(libVersion.Id, "A", 1), new MethodologyCategory(libVersion.Id, "B", 2));
        AddConstant(db, libVersion.Id, "K", 1.5m, unitKg, category: "A");
        AddConstant(db, libVersion.Id, "K", 2.5m, unitKg, category: "B");

        // ---- Основна методологія V1 ----
        db.MethodologyImports.Add(new MethodologyImport(mainV1.Id, library.Id, main.Id));
        var mass = new MethodologyFormula(mainV1.Id, EcrCode.Create("Mass"), "@Qty * 2");
        mass.SetScope(MethodologyFormulaScope.Row);
        mass.SetVisible(true);
        mass.SetOutputUnit(unitT);
        mass.SetEvaluationOrder(1);
        var emission = new MethodologyFormula(mainV1.Id, EcrCode.Create("Emission"), "!Mass * CST.EF * !Dens");
        emission.SetEvaluationOrder(2);
        db.MethodologyFormulas.AddRange(mass, emission);
        db.MethodologyOutputs.Add(new MethodologyOutput(mainV1.Id, EcrCode.Create("Emission"), unitT, 1));
        db.MethodologyCategories.AddRange(
            new MethodologyCategory(mainV1.Id, "A", 1), new MethodologyCategory(mainV1.Id, "B", 2));
        db.MethodologyCategoryRules.Add(new MethodologyCategoryRule(mainV1.Id, "if(@Zone = 'North', 'A', 'B')", Now));
        db.MethodologySubstances.AddRange(
            new MethodologySubstance(mainV1.Id, e301.Id, 1), new MethodologySubstance(mainV1.Id, e304.Id, 2));

        // EF: речовина 0301 — два вікна чинності; 0304 — одне.
        AddConstant(db, mainV1.Id, "EF", 0.5m, unitKg, substanceEntryId: e301.Id, validTo: new DateOnly(2025, 6, 1));
        AddConstant(db, mainV1.Id, "EF", 0.6m, unitKg, substanceEntryId: e301.Id, validFrom: new DateOnly(2025, 6, 1));
        AddConstant(db, mainV1.Id, "EF", 0.7m, unitKg, substanceEntryId: e304.Id, validFrom: new DateOnly(2000, 1, 1));

        // Правила: запис довідника (Id запису числом) + текст; неактивне правило не береться.
        db.MethodologyRules.AddRange(
            new MethodologyRule(
                mainV1.Id, EcrCode.Create("R_GAS"),
                $"{{\"{fuelColumn.Id}\":{gas.Id},\"{zoneColumn.Id}\":\"North\"}}", 1),
            new MethodologyRule(mainV1.Id, EcrCode.Create("R_SOUTH"), $"{{\"{zoneColumn.Id}\":\"South\"}}", 2));
        var inactive = new MethodologyRule(mainV1.Id, EcrCode.Create("R_OFF"), $"{{\"{zoneColumn.Id}\":\"Off\"}}", 3);
        inactive.SetActive(false);
        db.MethodologyRules.Add(inactive);

        // ---- Основна методологія V2: без правил (конвертація має дати помилку rowsNoRules) ----
        var v2Formula = new MethodologyFormula(mainV2.Id, EcrCode.Create("Mass"), "@Qty");
        v2Formula.SetScope(MethodologyFormulaScope.Row);
        v2Formula.SetEvaluationOrder(1);
        db.MethodologyFormulas.Add(v2Formula);

        // ---- Чернетка: формули створено «не в тому порядку» ----
        var b = new MethodologyFormula(draftVersion.Id, EcrCode.Create("B"), "!A + 1");
        b.SetScope(MethodologyFormulaScope.Row);
        var a = new MethodologyFormula(draftVersion.Id, EcrCode.Create("A"), "@Qty");
        a.SetScope(MethodologyFormulaScope.Row);
        db.MethodologyFormulas.AddRange(b, a);

        // ---- Версія з битими правилами: колонка, якої немає, і не-JSON ----
        var brokenFormula = new MethodologyFormula(brokenVersion.Id, EcrCode.Create("X"), "@Qty");
        brokenFormula.SetScope(MethodologyFormulaScope.Row);
        brokenFormula.SetEvaluationOrder(1);
        db.MethodologyFormulas.Add(brokenFormula);
        db.MethodologyRules.AddRange(
            new MethodologyRule(brokenVersion.Id, EcrCode.Create("R_NOCOL"), "{\"999999\":\"x\"}", 1),
            new MethodologyRule(brokenVersion.Id, EcrCode.Create("R_JSON"), "not json", 2));

        // ---- Версія на Script ----
        scriptedVersion.SetScript("rows from MS_T1;", Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ---- Прив'язки: активна на T1, неактивна на T2 (її таблиця теж потрапляє в rows) ----
        db.CalculationBindings.Add(new CalculationBinding(table1.Id, qtyColumn.Id, main.Id, "Emission", "{}"));
        var second = new CalculationBinding(table2.Id, qty2Column.Id, main.Id, "Emission", "{}");
        second.Update("{}", false);
        db.CalculationBindings.Add(second);
        db.CalculationBindings.Add(new CalculationBinding(table2.Id, qty2Column.Id, broken.Id, "X", "{}"));
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ---- Публікація (дати чинності) ----
        libVersion.Publish(1, "seed", new DateOnly(2024, 1, 1), true, Now);
        mainV1.Publish(1, "seed", new DateOnly(2024, 1, 1), true, Now);
        mainV2.Publish(1, "seed", new DateOnly(2026, 1, 1), true, Now);
        moduleVersion.Publish(1, "seed", new DateOnly(2024, 1, 1), true, Now);
        scriptedVersion.Publish(1, "seed", new DateOnly(2024, 1, 1), true, Now);
        brokenVersion.Publish(1, "seed", new DateOnly(2024, 1, 1), true, Now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeedResult(
            libVersion.Id, mainV1.Id, mainV2.Id, draftVersion.Id, moduleVersion.Id, scriptedVersion.Id, brokenVersion.Id, gas.Id,
            running.Id);
    }

    private static void AddConstant(
        Ecr.Infrastructure.Persistence.EcrDbContext db, int versionId, string code, decimal value, int unitId,
        string? category = null, long? substanceEntryId = null, DateOnly? validFrom = null, DateOnly? validTo = null)
    {
        var constant = new MethodologyConstant(versionId, EcrCode.Create(code), value, unitId);
        constant.SetScope(category, substanceEntryId);
        constant.SetValidity(validFrom, validTo);
        db.MethodologyConstants.Add(constant);
    }
}
