// src/Ecr.Application/Templates/MonthInputAliasesHandler.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Templates;

/// <summary>Псевдонім місячного входу методології в запиті <c>PUT …/month-input-aliases</c> — адреса кодами.</summary>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="TableCode">Код таблиці (у межах аркуша).</param>
/// <param name="Alias">Ім'я аргументу методології (колишній код селектора / формули).</param>
/// <param name="MonthNumber">Місяць 1..12.</param>
/// <param name="ColumnCode">Код місячної колонки тієї самої таблиці з тим самим місяцем.</param>
public sealed record MonthInputAliasInput(
    string SheetCode, string TableCode, string Alias, int MonthNumber, string ColumnCode);

/// <summary>Тіло <c>PUT /template-versions/{id}/month-input-aliases</c>: ПОВНИЙ набір псевдонімів версії.</summary>
/// <param name="Items">Псевдоніми; порожній перелік — прибрати всі.</param>
public sealed record ReplaceMonthInputAliasesRequest(IReadOnlyList<MonthInputAliasInput> Items);

/// <summary>Псевдонім місячного входу у відповіді.</summary>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="Alias">Ім'я аргументу.</param>
/// <param name="MonthNumber">Місяць 1..12.</param>
/// <param name="ColumnCode">Код цільової колонки.</param>
/// <param name="ColumnDefId">Цільова колонка.</param>
public sealed record MonthInputAliasDto(
    string SheetCode, string TableCode, int TableDefId, string Alias, int MonthNumber, string ColumnCode, int ColumnDefId);

/// <summary>Набір псевдонімів місячних входів версії.</summary>
/// <param name="TemplateVersionId">Версія.</param>
/// <param name="Items">Псевдоніми в порядку аркуш → таблиця → ім'я → місяць.</param>
public sealed record MonthInputAliasesDto(int TemplateVersionId, IReadOnlyList<MonthInputAliasDto> Items);

/// <summary>
/// ✎ LAND-YEAR L0b (ADR-B74 Q3): читання й масова заміна псевдонімів місячних входів методологій
/// (<c>cfg.MonthInputAlias</c>) версії шаблону — шлях запису для конструктора Land 1.0.17 (<c>-Stage YearMonths</c>).
/// </summary>
/// <remarks>
/// <para>⛔ Запис — лише чернетка (<c>ECR-TMPL-0409</c> для опублікованої/застарілої): псевдонім змінює, яким місяцем
/// годується методологія, тобто це структура, а не презентація.</para>
/// <para>⛔ Набір перевіряється ТИМИ САМИМИ правилами, що й публікація (<see cref="PublishChecks.CheckMonthInputAliases"/>),
/// плюс адреса кодами (невідомий аркуш/таблиця/колонка), дублікати й довжина імені — усе одразу одним
/// <c>422 ECR-TMPL-0422</c> (<c>monthInputAliasInvalid</c>); нічого не записано.</para>
/// <para>Права: читання — <c>Template.View</c>, заміна — <c>Template.Edit</c> (як решта структурних правок версії).</para>
/// </remarks>
public sealed class MonthInputAliasesHandler(
    ITemplateVersionStore store,
    IMetadataCache metadataCache,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на заміну (структура версії).</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Право на читання.</summary>
    public const string ViewPermission = "Template.View";

    /// <summary>Найдовше ім'я псевдоніма (<c>cfg.MonthInputAlias.Alias nvarchar(64)</c>).</summary>
    public const int MaxAliasLength = 64;

    /// <summary>Псевдоніми версії.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    public async Task<MonthInputAliasesDto> GetAsync(int templateVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, ViewPermission, ct).ConfigureAwait(false);

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        var aliases = await store.ListMonthInputAliasesAsync(templateVersionId, ct).ConfigureAwait(false);

        return Map(version, aliases);
    }

    /// <summary>Замінює весь набір псевдонімів чернетки.</summary>
    /// <param name="templateVersionId">Версія-чернетка.</param>
    /// <param name="items">Новий набір.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Збережений набір.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">Набір не проходить перевірку (<c>ECR-TMPL-0422</c>).</exception>
    public async Task<MonthInputAliasesDto> ReplaceAsync(
        int templateVersionId, IReadOnlyList<MonthInputAliasInput> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                ErrorCodes.Unauthorized,
                "Сесія не містить користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var version = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        version.EnsureStructurallyMutable();

        var aliases = Resolve(version, items);
        var before = await store.ListMonthInputAliasesAsync(templateVersionId, ct).ConfigureAwait(false);

        version.TouchDraft(userId, clock.UtcNow);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await DraftVersionLock.EnsureDraftUnderLockAsync(store, version, innerCt).ConfigureAwait(false);

            await store.ReplaceMonthInputAliasesAsync(templateVersionId, aliases, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, nameof(MonthInputAlias), templateVersionId,
                    ChangeClass.Safe, "Replace",
                    Describe(before), Describe(aliases), ChangeReason: null,
                    ChangedByUserId: userId, CorrelationId: null),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Знімок версії несе псевдоніми (L0a) — інакше прогін методології читав би старий набір до перезбирання кешу.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);

        return Map(version, aliases);
    }

    /// <summary>
    /// Адреса кодами → сутності; усі хиби набору — одним <c>422</c>: невідома адреса, дублікат, довге ім'я, місяць поза
    /// 1..12 і правила публікації (<see cref="PublishChecks.CheckMonthInputAliases"/>).
    /// </summary>
    private static List<MonthInputAlias> Resolve(TemplateVersion version, IReadOnlyList<MonthInputAliasInput> items)
    {
        var tables = version.Sheets
            .Where(s => !s.IsDeleted)
            .SelectMany(s => s.Tables.Where(t => !t.IsDeleted).Select(t => (Sheet: s.Code, Table: t)))
            .ToDictionary(x => (x.Sheet, x.Table.Code), x => x.Table);

        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<(int, string, int)>();
        var aliases = new List<MonthInputAlias>(items.Count);
        foreach (var item in items)
        {
            var label = string.Create(
                CultureInfo.InvariantCulture, $"{item?.SheetCode}/{item?.TableCode}:{item?.Alias}@M{item?.MonthNumber:00}");
            if (item is null
                || string.IsNullOrWhiteSpace(item.Alias)
                || item.Alias.Length > MaxAliasLength
                || item.MonthNumber is < 1 or > 12
                || !tables.TryGetValue((item.SheetCode, item.TableCode), out var table))
            {
                unresolved.Add(label);
                continue;
            }

            // Невідома колонка — Id 0: CheckMonthInputAliases нижче відхилить її разом з рештою правил.
            var column = table.Columns.FirstOrDefault(c => !c.IsDeleted && c.Code == item.ColumnCode);
            if (!seen.Add((table.Id, item.Alias.ToUpperInvariant(), item.MonthNumber)))
            {
                unresolved.Add(label);
                continue;
            }

            aliases.Add(new MonthInputAlias(table.Id, item.Alias, (byte)item.MonthNumber, column?.Id ?? 0));
        }

        var diagnostics = new List<ExpressionDiagnostic>();
        if (unresolved.Count > 0)
        {
            var sample = string.Join(", ", unresolved.Take(3)) + (unresolved.Count > 3 ? ", …" : string.Empty);
            diagnostics.Add(new ExpressionDiagnostic(
                "ECR-TMPL-0422",
                $"Псевдоніми місячних входів некоректні — {unresolved.Count}: {sample}. Невідомий аркуш чи таблиця, "
                + $"дублікат, порожнє ім'я, ім'я довше за {MaxAliasLength} або місяць поза 1..12.",
                0,
                1,
                "err.ECR-TMPL-0422.monthInputAliasInvalid",
                new Dictionary<string, string>
                {
                    ["tableCode"] = string.Join(", ", unresolved.Select(u => u.Split(':')[0]).Distinct().Take(3)),
                    ["count"] = unresolved.Count.ToString(CultureInfo.InvariantCulture),
                    ["aliases"] = sample,
                }));
        }

        diagnostics.AddRange(PublishChecks.CheckMonthInputAliases(version, aliases));
        if (diagnostics.Count > 0)
        {
            throw ExpressionRejection.Build(
                diagnostics,
                "err.ECR-TMPL-0422.monthInputAliasInvalid",
                $"Псевдоніми місячних входів відхилено: знайдено проблем — {diagnostics.Count}.");
        }

        return aliases;
    }

    private static MonthInputAliasesDto Map(TemplateVersion version, IReadOnlyList<MonthInputAlias> aliases)
    {
        var tables = version.Sheets
            .SelectMany(s => s.Tables.Select(t => (Sheet: s.Code, Table: t)))
            .ToDictionary(x => x.Table.Id);

        return new MonthInputAliasesDto(
            version.Id,
            [.. aliases
                .Select(a =>
                {
                    var (sheet, table) = tables.TryGetValue(a.TableDefId, out var t) ? t : (string.Empty, null!);
                    var column = table?.Columns.FirstOrDefault(c => c.Id == a.ColumnDefId);
                    return new MonthInputAliasDto(
                        sheet, table?.Code ?? string.Empty, a.TableDefId, a.Alias, a.MonthNumber,
                        column?.Code ?? string.Empty, a.ColumnDefId);
                })
                .OrderBy(a => a.SheetCode, StringComparer.Ordinal)
                .ThenBy(a => a.TableCode, StringComparer.Ordinal)
                .ThenBy(a => a.Alias, StringComparer.Ordinal)
                .ThenBy(a => a.MonthNumber)]);
    }

    private static string Describe(IReadOnlyList<MonthInputAlias> aliases)
        => System.Text.Json.JsonSerializer.Serialize(
            aliases.Select(a => new { a.TableDefId, a.Alias, a.MonthNumber, a.ColumnDefId }));
}
