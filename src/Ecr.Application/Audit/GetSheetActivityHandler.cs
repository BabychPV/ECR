// src/Ecr.Application/Audit/GetSheetActivityHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Audit;

/// <summary>
/// Журнал дій на ОДНОМУ аркуші за період: правки значень людьми (сітка, імпорт, правка погоджувача) і дії робочого
/// процесу з коментарем; найновіші перші, keyset. Право <c>Document.View</c> (панель History в інспекторі).
/// </summary>
/// <remarks>
/// ⛔ Доступ вирішує <see cref="GetDocumentHandler"/> (чужий і неіснуючий документ - однакова <c>404</c>, як у
/// <c>GET /documents/{id}</c>), далі - право і грант на проєкт, далі межі читання ПЕРІОДУ:
/// аркуш, який читач не бачить, відповідає тією ж <c>404</c>, а колонки закритих таблиць не потрапляють у SQL-фільтр
/// до <c>TOP</c> - їхніх правок немає ні на сторінці, ні в курсорі, ні в підрахунках (підрахунків у відповіді
/// немає навмисно). Це НЕ загальний журнал аудиту: його поріг лишається <c>Security.ViewAudit</c>.
///
/// ⛔ Q-17 (LAND-YEAR G1): правку колонки таблиці «місяці в колонках», якою володіє інший період (не-місячна — якір
/// року; M{m} — період m), PATCH пише в період-власник, тож журнал періоду перегляду її не бачив. Тепер журнал аркуша
/// показує й такі правки (рядок несе період-власник) — для колонок, які читач бачить на логічній адресі І в
/// періоді-власнику (D-214); пари йдуть у ТОЙ САМИЙ запит журналу (<see cref="Ports.CellChangeFilter.OwnerPeriodColumns"/>).
/// Маршрутизація вимкнена чи таблиці поза Q-17 — запит і кількість звернень як були.
/// </remarks>
public sealed class GetSheetActivityHandler(
    GetDocumentHandler getDocument,
    SheetActivityQuery query,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    Ports.IMonthColumnRouter? monthRouter = null,
    Ports.IMetadataCache? metadata = null,
    // ✎ B73: родовід колонок через перенос версії (як V-4 у GetCellChangesHandler). Без нього — як доти.
    Ports.IColumnPathMapper? columnPaths = null)
{
    /// <summary>Право читання журналу аркуша - те саме, що й читання документа.</summary>
    /// <remarks>Названо тут, бо статичний сторож читає тіло класу й за впровадженим обробником не ходить.</remarks>
    public const string Permission = ListDocumentsHandler.Permission;

    /// <summary>Розмір сторінки за замовчуванням.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Стеля розміру сторінки.</summary>
    public const int MaxLimit = 100;

    /// <summary>Повертає сторінку журналу аркуша.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Звітний період.</param>
    /// <param name="byUserId">Лише дії цього користувача; <c>null</c> - усі.</param>
    /// <param name="includeSystem">Додати системні зміни значень (перерахунок, інтеграція, міграція).</param>
    /// <param name="limit">Розмір сторінки; <c>0</c> - 50; більше за 100 - 422.</param>
    /// <param name="cursor">Курсор наступної сторінки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="year">
    /// ✎ LAND-YEAR L6: документ річної розкладки — журнал УСІХ місяців року періоду, злитий за часом (рядок несе свій
    /// період). Межі кожного місяця — СВОЇ (D-214): колонки, таблиці й аркуш, які читач бачить саме в тому місяці. Інша
    /// розкладка — як без прапора.
    /// </param>
    public async Task<SheetActivityPage> HandleAsync(
        long documentId, int sheetDefId, int periodKey, int? byUserId, bool includeSystem, int limit, string? cursor,
        CancellationToken ct, bool year = false)
    {
        var key = PeriodKey.Parse(periodKey);
        var size = limit == 0 ? DefaultLimit : limit;
        if (size is < 1 or > MaxLimit)
        {
            throw new BusinessRuleException(
                Ecr.Domain.Errors.ErrorCodes.RequestInvalid, $"Розмір сторінки поза межами 1..{MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = MaxLimit.ToString(CultureInfo.InvariantCulture),
                });
        }

        var document = await getDocument.HandleAsync(documentId, periodKey, ct).ConfigureAwait(false)
                       ?? throw NotFound(documentId);

        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        await DocumentVisibility
            .RequireInDocumentAsync(access, profile, documentId, document.ProjectId, Permission, ct).ConfigureAwait(false);

        var anyPeriod = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        var readable = anyPeriod.InPeriod(key);

        // Аркуш невідомий знімку або закритий читачеві - така сама відповідь, як на неіснуючий документ.
        var sheetCode = readable.SheetCodeOf(sheetDefId);
        var columns = readable.VisibleColumnIdsOfSheet(sheetDefId);
        if (sheetCode is null || columns is null)
        {
            throw NotFound(documentId);
        }

        // ⛔ Журнал рядків — лише таблиці, які читач бачить у цьому періоді (схована таблиця «не існує», D-6).
        var tables = readable.VisibleTableIdsOfSheet(sheetDefId) ?? [];

        // Q-17 (G1): правки колонок з іншим власником — з періоду-власника; лише звужує (логічна адреса + D-214 власника).
        var owners = await OwnerPeriodColumns
            .CollectAsync(monthRouter, metadata, anyPeriod, documentId, key, columns, ct, sheetDefId: sheetDefId)
            .ConfigureAwait(false);

        // ✎ LAND-YEAR L6: річний режим — інші місяці року парами «період → колонки, видимі В ТОМУ місяці» в тому самому
        // запиті журналу правок; журнал процесу й рядків — по місяцях зі своїми межами.
        IReadOnlyList<SheetActivityPeriod>? yearPeriods = null;
        IReadOnlyList<int>? otherPeriods = null;
        var months = year
            ? await YearDocumentPeriods.ResolveAsync(monthRouter, documentId, key, ct).ConfigureAwait(false)
            : null;
        if (months is not null)
        {
            (owners, otherPeriods, yearPeriods) = YearScopes(anyPeriod, sheetDefId, periodKey, months, columns, owners);
        }

        // ✎ B73 (V-4 для журналу аркуша): `aud.CellChange` незмінний і після переносу проєкту лишається на Id колонок
        // старої версії — межі журналу = видимі колонки поточної версії РАЗОМ з їхнім родоводом (і в парах власників).
        (columns, owners) = await WithLineageAsync(columns, owners, ct).ConfigureAwait(false);

        // ✎ B102: так само `aud.RowChange` лишається на TableDefId старої версії — межі журналу рядків = видимі таблиці
        // РАЗОМ з їхнім родоводом (лише від уже видимих: схована таблиця не дає й свого родоводу).
        tables = await WithTableLineageAsync(tables, ct).ConfigureAwait(false);
        if (yearPeriods is not null)
        {
            var widened = new List<SheetActivityPeriod>(yearPeriods.Count);
            foreach (var p in yearPeriods)
            {
                widened.Add(p with { VisibleTableIds = await WithTableLineageAsync(p.VisibleTableIds, ct).ConfigureAwait(false) });
            }

            yearPeriods = widened;
        }

        return await query
            .ReadAsync(
                new SheetActivityScope(documentId, periodKey, sheetCode, columns, byUserId, includeSystem, tables, owners)
                {
                    YearPeriods = yearPeriods,
                    OtherPeriodKeys = otherPeriods,
                },
                size, cursor, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// ✎ LAND-YEAR L6: межі інших місяців року — кожен своїми межами читання (<c>InPeriod(m)</c>, D-214): аркуш, закритий
    /// у місяці, не дає ні правок, ні подій, ні рядків цього місяця.
    /// </summary>
    /// <remarks>
    /// Місяць, де читач бачить усі колонки перегляду (звичайний випадок), — одним ключем періоду
    /// (<see cref="Ports.CellChangeFilter.OtherPeriodKeys"/>; видимі колонки перегляду діють і на нього); місяць із вужчими
    /// межами — парами «місяць → колонки, видимі в ньому» (на T9_07 ≈ 590 колонок пари множили б JSON-параметр).
    /// </remarks>
    private static (IReadOnlyList<Ports.CellChangeOwnerColumns>? Owners, IReadOnlyList<int>? OtherPeriods,
        IReadOnlyList<SheetActivityPeriod> Periods) YearScopes(
        DocumentReadScope anyPeriod, int sheetDefId, int viewPeriod, IReadOnlyList<int> months,
        IReadOnlyList<int> viewColumns, IReadOnlyList<Ports.CellChangeOwnerColumns>? owners)
    {
        var pairs = owners?.ToList() ?? [];
        var whole = new List<int>();
        var periods = new List<SheetActivityPeriod>(months.Count);
        foreach (var month in months.Where(m => m != viewPeriod))
        {
            var readable = anyPeriod.InPeriod(new PeriodKey(month));
            if (readable.SheetCodeOf(sheetDefId) is not { } monthSheet || !readable.CanReadSheetCode(monthSheet))
            {
                continue;
            }

            if (readable.VisibleColumnIdsOfSheet(sheetDefId) is { Count: > 0 } monthColumns)
            {
                var set = monthColumns.ToHashSet();
                if (viewColumns.All(set.Contains))
                {
                    whole.Add(month);
                }
                else
                {
                    pairs.Add(new Ports.CellChangeOwnerColumns(month, monthColumns));
                }
            }

            periods.Add(new SheetActivityPeriod(month, readable.VisibleTableIdsOfSheet(sheetDefId) ?? []));
        }

        return (pairs.Count == 0 ? null : pairs, whole.Count == 0 ? null : whole, periods);
    }

    /// <summary>
    /// ✎ B73: видимі колонки аркуша й колонки пар «період-власник» — разом з Id тих самих колонок у інших версіях шаблону
    /// (<see cref="Ports.IColumnPathMapper.LineageAsync"/>, ОДИН виклик на запит).
    /// </summary>
    /// <remarks>
    /// ⛔ Лише розширення від ВЖЕ видимих: колонка під Deny (чи поза межею D-214 власника) у <paramref name="columns"/> /
    /// <paramref name="owners"/> не потрапила, тож і її родоводу немає — правки до переносу так само приховані.
    /// Без маппера чи без інших версій — набори дослівно як були.
    /// </remarks>
    private async Task<(IReadOnlyList<int> Columns, IReadOnlyList<Ports.CellChangeOwnerColumns>? Owners)> WithLineageAsync(
        IReadOnlyList<int> columns, IReadOnlyList<Ports.CellChangeOwnerColumns>? owners, CancellationToken ct)
    {
        if (columnPaths is null || columns.Count == 0)
        {
            return (columns, owners);
        }

        var lineage = await columnPaths.LineageAsync(columns, ct).ConfigureAwait(false);

        IReadOnlyList<int> Widen(IEnumerable<int> ids)
            => [.. ids.SelectMany(id => lineage.GetValueOrDefault(id) ?? [id]).Distinct().Order()];

        var widened = Widen(columns);
        var widenedOwners = owners is null
            ? null
            : (IReadOnlyList<Ports.CellChangeOwnerColumns>)[.. owners.Select(o => o with { ColumnDefIds = Widen(o.ColumnDefIds) })];

        return (widened, widenedOwners);
    }

    /// <summary>
    /// ✎ B102: видимі таблиці аркуша разом з Id тих самих таблиць у інших версіях шаблону
    /// (<see cref="Ports.IColumnPathMapper.TableLineageAsync"/>, ОДИН виклик на запит).
    /// </summary>
    /// <remarks>⛔ Лише розширення від ВЖЕ видимих (Deny таблиці відсікає до родоводу). Без маппера — як було.</remarks>
    private async Task<IReadOnlyList<int>> WithTableLineageAsync(IReadOnlyList<int> tables, CancellationToken ct)
    {
        if (columnPaths is null || tables.Count == 0)
        {
            return tables;
        }

        var lineage = await columnPaths.TableLineageAsync(tables, ct).ConfigureAwait(false);
        return [.. tables.SelectMany(id => lineage.GetValueOrDefault(id) ?? [id]).Distinct().Order()];
    }

    private static NotFoundException NotFound(long documentId)
        => new(
            "ECR-DOC-0404",
            $"Документ {documentId} не знайдено.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });
}
