// src/Ecr.Application/Calculations/ImportMethodologyPackageHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// Імпорт пакета <c>ecr-methodology-package</c> v1 (крок V, FEATURE-HSE301-VIEW §11.6):
/// методології, версії-чернетки, формули, константи й імпорти між методологіями.
/// </summary>
/// <remarks>
/// ⛔ Лише ЧЕРНЕТКИ, через ті самі доменні входи, що й конфігуратор
/// (<c>Methodology.AddVersion</c>, <c>MethodologyVersion.AddFormula</c>,
/// <c>AddNumericConstant</c>/<c>AddTextConstant</c>, <c>MethodologyImport</c>). Публікація —
/// окрема дія іншої людини: чотири ока, причина, золотий набір (<c>D-40</c>, ФВ-9.12) не
/// обходяться імпортом.
/// <para>
/// ⛔ Усе або нічого: блокер (нерезолвне посилання, недопустимий код, блокер експортера) —
/// <c>422</c> зі звітом; розбіжність із наявною версією того самого номера — <c>409</c> зі
/// звітом. В обох випадках не пишеться нічого. Той самий пакет удруге — звіт <c>unchanged</c>
/// і жодного запису: версія, що вже є з тим самим вмістом, не дублюється.
/// </para>
/// </remarks>
public sealed class ImportMethodologyPackageHandler(
    IMethodologyDraftStore drafts,
    IMethodologyStore methodologies,
    IUnitCatalog units,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на імпорт (`02-contracts.md` §9) — як на заведення методології.</summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Друге право: імпорт заводить і константи.</summary>
    public const string ConstantPermission = "Calculation.EditConstant";

    /// <summary>Пояс за замовчуванням — майданчик замовника.</summary>
    public const string DefaultTimeZone = "Asia/Atyrau";

    /// <summary>Будує план і, якщо це не сухий прогін, записує його.</summary>
    /// <param name="package">Пакет.</param>
    /// <param name="dryRun"><c>true</c> — нічого не писати, лише звіт.</param>
    /// <param name="timeZoneId">Пояс майданчика (IANA); <c>null</c> — <see cref="DefaultTimeZone"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Звіт.</returns>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0422</c> — блокери; <c>ECR-CALC-0409</c> — конфлікти з наявними версіями.
    /// </exception>
    public async Task<MethodologyImportReportDto> HandleAsync(
        MethodologyPackageDto package, bool dryRun, string? timeZoneId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await PermissionCheck.RequireAsync(access, currentUser, ConstantPermission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401",
                "Анонімний запит не імпортує методологій.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var timeZone = ResolveTimeZone(timeZoneId);
        var existing = await LoadExistingAsync(package, ct).ConfigureAwait(false);
        var catalog = await units.GetAsync(ct).ConfigureAwait(false);

        var plan = MethodologyPackagePlanner.Plan(package, existing, catalog, timeZone);

        if (dryRun)
        {
            return plan.ToReport(dryRun: true, applied: false);
        }

        if (plan.Blockers.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Імпорт пакета відхилено: блокерів — {plan.Blockers.Count}. Нічого не записано.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.methodologyImportBlocked",
                    ["count"] = plan.Blockers.Count.ToString(CultureInfo.InvariantCulture),
                    ["report"] = plan.ToReport(dryRun: false, applied: false),
                });
        }

        if (plan.Conflicts.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Імпорт пакета відхилено: версій з іншим вмістом — {plan.Conflicts.Count}. Нічого не записано.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.methodologyImportConflict",
                    ["count"] = plan.Conflicts.Count.ToString(CultureInfo.InvariantCulture),
                    ["report"] = plan.ToReport(dryRun: false, applied: false),
                });
        }

        if (!plan.HasChanges)
        {
            return plan.ToReport(dryRun: false, applied: false);
        }

        var methodologyIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var versionIds = new Dictionary<(string, string), int>();

        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                await WriteAsync(plan, existing, userId, methodologyIds, versionIds, innerCt).ConfigureAwait(false);

                var report = plan.ToReport(dryRun: false, applied: true, methodologyIds, versionIds);

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "calc.MethodologyPackage",
                        EntityId: 0,
                        ChangeClass: ChangeClass.Guarded,
                        Operation: "ImportPackage",
                        OldJson: null,
                        NewJson: JsonSerializer.Serialize(new
                        {
                            report.Totals,
                            versions = report.Methodologies
                                .SelectMany(m => m.Versions
                                    .Where(v => v.Action == MethodologyPackagePlanner.Create)
                                    .Select(v => new { methodology = m.Code, v.Version, v.VersionId })),
                        }),
                        ChangeReason: $"Імпорт пакета {MethodologyPackagePlanner.FormatName} v{package.Version}",
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    innerCt).ConfigureAwait(false);

                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return plan.ToReport(dryRun: false, applied: true, methodologyIds, versionIds);
    }

    /// <summary>Пише план у поточній транзакції.</summary>
    private async Task WriteAsync(
        MethodologyImportPlan plan,
        IReadOnlyDictionary<string, ExistingMethodology> existing,
        int userId,
        Dictionary<string, int> methodologyIds,
        Dictionary<(string, string), int> versionIds,
        CancellationToken ct)
    {
        methodologyIds.Clear();
        versionIds.Clear();

        foreach (var (code, current) in existing)
        {
            methodologyIds[code] = current.Id;
        }

        var created = new List<(string Code, Methodology Entity)>();
        foreach (var m in plan.Methodologies.Where(m => m.ExistingId is null
                     && m.Versions.Any(v => v.Action == MethodologyPackagePlanner.Create)))
        {
            var entity = new Methodology(
                EcrCode.Create(m.Code),
                new LocalizedText(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = m.Code }));
            entity.SetKind(m.Kind);
            drafts.AddMethodology(entity);
            created.Add((m.Code, entity));
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var (code, entity) in created)
        {
            methodologyIds[code] = entity.Id;
        }

        foreach (var m in plan.Methodologies)
        {
            foreach (var v in m.Versions.Where(v => v.Action == MethodologyPackagePlanner.Create))
            {
                var methodologyId = methodologyIds[m.Code];
                var aggregate = await drafts.FindAsync(methodologyId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Методологія {methodologyId} зникла під час імпорту.");

                var draft = new MethodologyVersion(
                    methodologyId, v.Version, CalculationLevel.Configuration, userId, clock.UtcNow);

                // Номер версії перевіряє агрегат (`UQ_MethodologyVersion`), як і в конфігураторі.
                aggregate.AddVersion(draft);
                var versionId = await drafts.SaveDraftAsync(draft, copyFromVersionId: null, ct).ConfigureAwait(false);
                versionIds[(m.Code, v.Version)] = versionId;

                foreach (var f in v.Content.Formulas)
                {
                    var formula = draft.AddFormula(EcrCode.Create(f.Code), f.Expression, FormulaResultType.Number, null);
                    formula.SetArguments(f.ArgumentsCsv);
                    drafts.Add(formula);
                }

                foreach (var c in v.Content.Constants)
                {
                    var code = EcrCode.Create(c.Code);
                    var constant = c switch
                    {
                        { Kind: ConstantKind.Numeric, Value: { } value, UnitId: { } unit }
                            => draft.AddNumericConstant(code, value, unit),
                        { Kind: ConstantKind.Numeric }
                            => MethodologyConstant.FromImport(versionId, code, c.TextValue, ConstantKind.Numeric, c.UnitId),
                        _ => draft.AddTextConstant(code, c.TextValue ?? string.Empty, c.Kind),
                    };

                    constant.SetScope(c.Category, null);
                    constant.SetValidity(c.ValidFrom, c.ValidTo);
                    constant.SetSource(c.Source);
                    drafts.Add(constant);
                }

                foreach (var imported in v.Content.Imports)
                {
                    drafts.Add(new MethodologyImport(versionId, methodologyIds[imported], methodologyId));
                }

                await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Наявні методології пакета і його бібліотеки — з вмістом потрібних версій.</summary>
    private async Task<IReadOnlyDictionary<string, ExistingMethodology>> LoadExistingAsync(
        MethodologyPackageDto package, CancellationToken ct)
    {
        var library = string.IsNullOrWhiteSpace(package.Library) ? "Common" : package.Library.Trim();
        // ⚠ GroupBy, а не ToDictionary: дубль назви в іншому регістрі (`HSE400`/`hse400`) — блокер
        // планувальника `duplicateMethodology`, а не ArgumentException → 500 ще до нього (аудит L7-09).
        var wanted = (package.Methodologies ?? [])
            .GroupBy(m => m.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new HashSet<string>(g.SelectMany(m => m.Versions ?? []).Select(v => v.Version), StringComparer.Ordinal),
                StringComparer.OrdinalIgnoreCase);

        var libraryInPackage = wanted.ContainsKey(library);
        var codes = wanted.Keys.Append(library).Where(c => EcrCode.TryCreate(c, out _)).Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in codes)
        {
            var methodology = await drafts.FindByCodeAsync(code, ct).ConfigureAwait(false);
            if (methodology is null)
            {
                continue;
            }

            var versions = new List<ExistingMethodologyVersion>();
            foreach (var version in await drafts.GetAllVersionsAsync(methodology.Id, ct).ConfigureAwait(false))
            {
                // Вміст — лише тих версій, з якими звіряємо, і всіх версій бібліотеки поза пакетом.
                var needed = (wanted.TryGetValue(code, out var numbers) && numbers.Contains(version.Version))
                    || (!libraryInPackage && string.Equals(code, library, StringComparison.OrdinalIgnoreCase));

                var content = needed
                    ? await ContentAsync(version.Id, ct).ConfigureAwait(false)
                    : new ImportVersionContent([], [], []);

                versions.Add(new ExistingMethodologyVersion(
                    version.Id, version.Version, version.Status == TemplateVersionStatus.Draft, content));
            }

            result[code] = new ExistingMethodology(methodology.Id, methodology.Code, methodology.Kind, versions);
        }

        return result;
    }

    private async Task<ImportVersionContent> ContentAsync(int versionId, CancellationToken ct)
    {
        var formulas = await methodologies.GetFormulasAsync(versionId, ct).ConfigureAwait(false);
        var constants = await methodologies.GetConstantsAsync(versionId, ct).ConfigureAwait(false);
        var imports = await drafts.GetImportedMethodologyCodesAsync(versionId, ct).ConfigureAwait(false);

        return new ImportVersionContent(
            [.. formulas.Select(f => new ImportFormulaContent(f.Code, f.Expression, f.ArgumentsCsv))],
            [.. constants.Select(c => new ImportConstantContent(
                c.Code, c.Kind, c.Value, c.TextValue, c.UnitId, c.Category, c.ValidFrom, c.ValidTo, c.Source))],
            imports);
    }

    /// <summary>Пояс майданчика за ідентифікатором IANA.</summary>
    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var id = string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZone : timeZoneId.Trim();

        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Пояс «{id}» невідомий: потрібен ідентифікатор IANA (напр. {DefaultTimeZone}).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.methodologyImportTimeZone",
                    ["timeZone"] = id,
                });
    }
}
