// src/Ecr.Infrastructure/Jobs/FlertRegistryProvisioner.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Flert;
using Ecr.Application.Registries.Rules;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Коди довідників дзеркала FLERT (тести підставляють свої, щоб не ділити спільну базу).</summary>
/// <param name="Stream">Потоки.</param>
/// <param name="Case">Кейси.</param>
/// <param name="Composition">Рядки складу.</param>
/// <param name="Component">Компоненти.</param>
public sealed record FlertRegistryCodes(string Stream, string Case, string Composition, string Component)
{
    /// <summary>Коди §11.4: <c>STREAM</c>, <c>STREAM_CASE</c>, <c>GAS_COMPOSITION</c>, <c>COMPONENT</c>.</summary>
    public static FlertRegistryCodes Default { get; } = new(
        FlertCompositionSchema.StreamRegistry,
        FlertCompositionSchema.CaseRegistry,
        FlertCompositionSchema.CompositionRegistry,
        FlertCompositionSchema.ComponentRegistry);
}

/// <summary>Довідники дзеркала, готові до запису.</summary>
/// <param name="Stream">Потоки.</param>
/// <param name="Case">Кейси.</param>
/// <param name="Composition">Рядки складу.</param>
/// <param name="Component">Компоненти.</param>
/// <param name="Created">Коди довідників, створених цим викликом.</param>
/// <param name="ComponentsAdded">Коди компонентів, доданих цим викликом.</param>
/// <param name="Problems">Чому дзеркало писати не можна (несумісний наявний довідник); порожньо — можна.</param>
/// <param name="Warnings">Що варто знати (довідник дзеркала не <c>External</c>).</param>
public sealed record FlertRegistrySet(
    RegistryDef? Stream,
    RegistryDef? Case,
    RegistryDef? Composition,
    RegistryDef? Component,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> ComponentsAdded,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Крок FLERT F0 (<c>D-476</c>): довідники <c>STREAM</c> → <c>STREAM_CASE</c> → <c>GAS_COMPOSITION</c> і
/// <c>COMPONENT</c> за FEATURE-REGISTRY-TABLES §11.4 з правилом <c>SUM_100</c>, якщо їх немає.
/// </summary>
/// <remarks>
/// ⛔ Не <c>seed/features/*.sql</c>: файл сіду фічі несе лише рядки інтерфейсу, а довідники заводить код
/// (той самий принцип, що <c>ProvisionAirLimitsHandler.EnsureAreaRegistryAsync</c>). Міграції не потрібно:
/// «зовнішнє джерело» — це вже наявне <see cref="RegistryDef.SourceKind"/> = <see cref="RegistrySourceKind.External"/>
/// (ручна правка відхиляється <c>ExternalRegistryGuard</c>, <c>D-211</c>).
/// <para>
/// ⚠ Ідемпотентно і НЕ руйнівно: наявний довідник не редагується (ні поля, ні ключі, ні джерело) — лише
/// перевіряється, що в ньому є поля, які пише синк, потрібного типу. Несумісний — <see cref="FlertRegistrySet.Problems"/>,
/// синк не пише нічого. Наявний довідник дзеркала не <c>External</c> — попередження (перемикання master —
/// небезпечне право людини, <c>SwitchRegistrySourceHandler</c>). Виняток один: у <c>COMPONENT</c> (master — ECR)
/// додаються ВІДСУТНІ коди компонентів FLERT, наявні записи не чіпаються.
/// </para>
/// <para>
/// ⚠ Автор — поточний користувач області (<c>svc-integration</c> задачі синку); кожен створений довідник —
/// рядок <c>aud.StructureChange</c> (<c>ProvisionFlert</c>). Виклик — усередині транзакції задачі.
/// </para>
/// <para>
/// ⚠ Відхилення від §11.4 (див. <see cref="FlertCompositionSchema"/>): <c>STREAM_CASE.STREAM</c> — посилання, а не
/// композиція (кейс темпоральний); поля <c>MW</c>/<c>N_C</c>/<c>N_S</c> компонента НЕ обов'язкові: значення бібліотеки
/// HYSYS — факт замовника (§11.4, <c>R-27</c>), їх вносить адміністратор або імпорт Add Stream, а не синк FLERT.
/// </para>
/// </remarks>
public sealed class FlertRegistryProvisioner(
    EcrDbContext db, IUnitOfWork uow, IClock clock, IAuditWriter audit, Application.Common.ICurrentUser currentUser)
{
    /// <summary>Операція рядка <c>aud.StructureChange</c> створеного довідника.</summary>
    public const string ProvisionOperation = "ProvisionFlert";

    private static readonly (string Code, string En, string Ru, CellDataType Type, bool Required)[] StreamFields =
    [
        (FlertCompositionSchema.StreamLegacyId, "FLERT stream Id", "Id потока FLERT", CellDataType.Int, true),
        (FlertCompositionSchema.StreamNumber, "Stream number", "Номер потока", CellDataType.String, false),
        (FlertCompositionSchema.StreamName, "Name", "Название", CellDataType.String, false),
        (FlertCompositionSchema.StreamIsOnshore, "Onshore", "Суша", CellDataType.Bool, false),
        (FlertCompositionSchema.StreamIsSour, "Sour", "Кислый", CellDataType.Bool, false),
        (FlertCompositionSchema.StreamIsHmb, "HMB stream", "Поток HMB", CellDataType.Bool, false),
        (FlertCompositionSchema.StreamHmbDoc, "HMB document", "Документ HMB", CellDataType.String, false),
        (FlertCompositionSchema.StreamGroup, "Group", "Группа", CellDataType.Int, false),
        (FlertCompositionSchema.StreamExcludeInList, "Hidden in lists", "Скрыт в списках", CellDataType.Bool, false),
    ];

    private static readonly (string Code, string En, string Ru, CellDataType Type, bool Required)[] CaseFields =
    [
        (FlertCompositionSchema.CaseStream, "Stream", "Поток", CellDataType.Lookup, true),
        (FlertCompositionSchema.CaseName, "Case", "Кейс", CellDataType.String, true),
        (FlertCompositionSchema.CaseTemperature, "T, °C", "T, °C", CellDataType.Decimal, false),
        (FlertCompositionSchema.CaseDensityOp, "ρ op (HYSYS)", "ρ раб. (HYSYS)", CellDataType.Decimal, false),
        (FlertCompositionSchema.CaseMwHysys, "MW (HYSYS)", "MW (HYSYS)", CellDataType.Decimal, false),
        (FlertCompositionSchema.CaseZ, "Z", "Z", CellDataType.Decimal, false),
        (FlertCompositionSchema.CaseRuleParse, "FLERT rule", "Правило FLERT", CellDataType.String, false),
        (FlertCompositionSchema.CaseLegacyTypeId, "FLERT case Id", "Id кейса FLERT", CellDataType.Int, false),
        (FlertCompositionSchema.CaseLegacyCompositionId, "FLERT composition Id", "Id состава FLERT", CellDataType.Int, false),
        (FlertCompositionSchema.CaseNoComposition, "No composition", "Нет состава", CellDataType.Bool, false),
    ];

    private static readonly (string Code, string En, string Ru, CellDataType Type, bool Required)[] CompositionFields =
    [
        (FlertCompositionSchema.RowCase, "Case", "Кейс", CellDataType.Lookup, true),
        (FlertCompositionSchema.RowComponent, "Component", "Компонент", CellDataType.Lookup, true),
        (FlertCompositionSchema.RowMolPct, "mol %", "мол. %", CellDataType.Decimal, true),
    ];

    private static readonly (string Code, string En, string Ru, CellDataType Type, bool Required)[] ComponentFields =
    [
        ("FORMULA", "Formula", "Формула", CellDataType.String, false),
        (FlertCompositionSchema.ComponentHysysName, "HYSYS / FLERT name", "Имя HYSYS / FLERT", CellDataType.String, false),
        ("MW", "Molar mass, g/mol", "Молярная масса, г/моль", CellDataType.Decimal, false),
        ("N_C", "Carbon atoms", "Атомов C", CellDataType.Int, false),
        ("N_H", "Hydrogen atoms", "Атомов H", CellDataType.Int, false),
        ("N_S", "Sulphur atoms", "Атомов S", CellDataType.Int, false),
        ("LHV", "LHV, MJ/Sm³", "НТС, МДж/ст.м³", CellDataType.Decimal, false),
        ("SOURCE", "Source of MW / LHV", "Источник MW / НТС", CellDataType.String, false),
    ];

    /// <summary>Заводить відсутні довідники й компоненти; наявні лише перевіряє.</summary>
    /// <param name="codes">Коди довідників.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<FlertRegistrySet> EnsureAsync(FlertRegistryCodes codes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var userId = currentUser.UserId
                     ?? throw new InvalidOperationException("Провізія довідників FLERT без автора (svc-integration).");
        var created = new List<string>();
        var problems = new List<string>();
        var warnings = new List<string>();

        var component = await FindAsync(codes.Component, ct).ConfigureAwait(false);
        if (component is null)
        {
            component = await CreateAsync(codes.Component, "Gas components", "Компоненты газа", isTemporal: false,
                RegistrySourceKind.Local, ComponentFields, _ => null, userId, ct).ConfigureAwait(false);
            created.Add(component.Code);
        }

        var stream = await FindAsync(codes.Stream, ct).ConfigureAwait(false);
        if (stream is null)
        {
            stream = await CreateAsync(codes.Stream, "FLERT streams", "Потоки FLERT", isTemporal: false,
                RegistrySourceKind.External, StreamFields, _ => null, userId, ct).ConfigureAwait(false);
            await AddKeyAsync(stream, "BY_LEGACY_ID", "By FLERT Id", isPrimary: false,
                [FlertCompositionSchema.StreamLegacyId], userId, ct).ConfigureAwait(false);
            created.Add(stream.Code);
        }
        else
        {
            Check(stream, StreamFields, external: true, problems, warnings);
        }

        var @case = await FindAsync(codes.Case, ct).ConfigureAwait(false);
        var caseCreated = false;
        if (@case is null)
        {
            var streamId = stream.Id;
            @case = await CreateAsync(codes.Case, "FLERT stream cases", "Кейсы потоков FLERT", isTemporal: true,
                RegistrySourceKind.External, CaseFields,
                f => f == FlertCompositionSchema.CaseStream ? (streamId, false) : null, userId, ct).ConfigureAwait(false);
            await AddKeyAsync(@case, "PK", "Stream + case", isPrimary: true,
                [FlertCompositionSchema.CaseStream, FlertCompositionSchema.CaseName], userId, ct).ConfigureAwait(false);
            created.Add(@case.Code);
            caseCreated = true;
        }
        else
        {
            Check(@case, CaseFields, external: true, problems, warnings);
        }

        var composition = await FindAsync(codes.Composition, ct).ConfigureAwait(false);
        if (composition is null)
        {
            var caseId = @case.Id;
            var componentId = component.Id;
            composition = await CreateAsync(codes.Composition, "FLERT gas composition", "Состав газа FLERT", isTemporal: false,
                RegistrySourceKind.External, CompositionFields,
                f => f switch
                {
                    FlertCompositionSchema.RowCase => (caseId, true),
                    FlertCompositionSchema.RowComponent => (componentId, false),
                    _ => null,
                },
                userId, ct).ConfigureAwait(false);
            await AddKeyAsync(composition, "PK", "Case + component", isPrimary: true,
                [FlertCompositionSchema.RowCase, FlertCompositionSchema.RowComponent], userId, ct).ConfigureAwait(false);
            created.Add(composition.Code);
        }
        else
        {
            Check(composition, CompositionFields, external: true, problems, warnings);
        }

        if (caseCreated)
        {
            // Правило — після складу: вираз шаблону називає дочірній довідник.
            await AddSum100Async(@case, composition.Code, ct).ConfigureAwait(false);
        }

        var added = await EnsureComponentsAsync(component, userId, ct).ConfigureAwait(false);

        foreach (var code in created)
        {
            var def = new[] { component, stream, @case, composition }.First(d => d.Code == code);
            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: "cfg.RegistryDef",
                    EntityId: def.Id,
                    ChangeClass.Guarded,
                    ProvisionOperation,
                    OldJson: null,
                    NewJson: JsonSerializer.Serialize(new { registry = def.Code, sourceKind = def.SourceKind.ToString(), isTemporal = def.IsTemporal }),
                    ChangeReason: "FLERT F0 (D-476): довідник дзеркала складу газу заведено задачею синку (FEATURE-REGISTRY-TABLES §11.4).",
                    userId,
                    currentUser.CorrelationId),
                ct).ConfigureAwait(false);
        }

        return new FlertRegistrySet(stream, @case, composition, component, created, added, problems, warnings);
    }

    private async Task<RegistryDef?> FindAsync(string code, CancellationToken ct)
        => await db.RegistryDefs
            .Include(d => d.Fields)
            .FirstOrDefaultAsync(d => d.Code == code, ct)
            .ConfigureAwait(false);

    private async Task<RegistryDef> CreateAsync(
        string code,
        string en,
        string ru,
        bool isTemporal,
        RegistrySourceKind sourceKind,
        (string Code, string En, string Ru, CellDataType Type, bool Required)[] fields,
        Func<string, (int Target, bool Composition)?> lookup,
        int userId,
        CancellationToken ct)
    {
        var def = new RegistryDef(EcrCode.Create(code), Text(en, ru), isTemporal);
        def.SwitchSource(sourceKind);
        def.UseCodeMode(RegistryCodeMode.Manual);
        def.MarkDefinitionUpdated(userId, clock.UtcNow);
        db.RegistryDefs.Add(def);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        var ordinal = 1;
        foreach (var (fieldCode, fieldEn, fieldRu, type, required) in fields)
        {
            var field = new RegistryFieldDef(def.Id, EcrCode.Create(fieldCode), Text(fieldEn, fieldRu), type, ordinal);
            field.Update(Text(fieldEn, fieldRu), ordinal, required);
            if (lookup(fieldCode) is { } target)
            {
                field.PointTo(target.Target);
                if (target.Composition)
                {
                    field.ComposeInto(ParentDeletePolicy.Cascade);
                }
            }

            db.RegistryFieldDefs.Add(field);
            ordinal++;
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await FindAsync(code, ct).ConfigureAwait(false))!;
    }

    private async Task AddKeyAsync(
        RegistryDef def, string code, string name, bool isPrimary, string[] fieldCodes, int userId, CancellationToken ct)
    {
        var fields = fieldCodes.Select(f => def.Fields.Single(x => x.Code == f)).ToList();
        db.RegistryKeyDefs.Add(new RegistryKeyDef(
            def.Id, EcrCode.Create(code), Text(name, name), fields, isPrimary, ignoreCase: true, userId, clock.UtcNow));
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task AddSum100Async(RegistryDef @case, string compositionCode, CancellationToken ct)
    {
        const decimal target = 100m;
        const decimal tolerance = 0.5m;
        var parameters = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [RegistryRuleTemplates.TemplateParameter] = RegistryRuleTemplates.ChildSum,
            [RegistryRuleTemplates.ChildParameter] = compositionCode,
            [RegistryRuleTemplates.FieldParameter] = FlertCompositionSchema.RowMolPct,
            [RegistryRuleTemplates.TargetParameter] = target,
            [RegistryRuleTemplates.ToleranceParameter] = tolerance,
        });

        db.RegistryRuleDefs.Add(new RegistryRuleDef(
            @case.Id,
            EcrCode.Create(FlertCompositionSchema.Sum100Rule),
            RegistryRuleKind.Expression,
            RegistryRuleTemplates.ChildSumExpression(
                compositionCode, FlertCompositionSchema.RowCase, FlertCompositionSchema.RowMolPct, target, tolerance),
            ValidationSeverity.Warning,
            Text("Σ mol % of the composition is not 100 ± 0.5", "Σ мол. % состава не равна 100 ± 0.5"),
            parameters));
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Відсутні в <c>COMPONENT</c> коди компонентів FLERT — з ім'ям колонки в <c>HYSYS_NAME</c>, якщо поле є.</summary>
    private async Task<IReadOnlyList<string>> EnsureComponentsAsync(RegistryDef component, int userId, CancellationToken ct)
    {
        var have = await db.RegistryEntries
            .Where(e => e.RegistryDefId == component.Id)
            .Select(e => e.Code)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var existing = have.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = FlertCompositionSchema.Components.Where(c => !existing.Contains(c)).ToList();
        if (missing.Count == 0)
        {
            return [];
        }

        var hysys = component.Fields.FirstOrDefault(f => f.Code == FlertCompositionSchema.ComponentHysysName && f.DataType == CellDataType.String);
        var nextOrdinal = have.Count + 1;
        var entries = new List<RegistryEntry>();
        foreach (var code in missing)
        {
            var entry = new RegistryEntry(component.Id, EcrCode.Create(code), Text(code, code), userId, clock.UtcNow);
            entry.SetOrdinal(nextOrdinal++);
            db.RegistryEntries.Add(entry);
            entries.Add(entry);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        if (hysys is not null)
        {
            foreach (var entry in entries)
            {
                var value = new RegistryValue(entry.Id, hysys.Id);
                value.Set(CellDataType.String, entry.Code, null);
                value.MarkChangedBy(userId);
                db.RegistryValues.Add(value);
            }

            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return missing;
    }

    /// <summary>Наявний довідник дзеркала: поля, які пише синк, є й мають потрібний тип.</summary>
    private static void Check(
        RegistryDef def,
        (string Code, string En, string Ru, CellDataType Type, bool Required)[] fields,
        bool external,
        List<string> problems,
        List<string> warnings)
    {
        foreach (var (code, _, _, type, _) in fields)
        {
            var field = def.Fields.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal));
            if (field is null)
            {
                problems.Add($"{def.Code}.{code}: поля немає");
            }
            else if (field.DataType != type)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{def.Code}.{code}: тип {field.DataType}, очікується {type}"));
            }
        }

        if (external && def.SourceKind != RegistrySourceKind.External)
        {
            warnings.Add($"{def.Code}: джерело {def.SourceKind}, а не External — ручні правки дзеркала не заборонені (D-476)");
        }
    }

    private static LocalizedText Text(string en, string ru)
        => new(new Dictionary<string, string> { ["en"] = en, ["ru"] = ru });
}
