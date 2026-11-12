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
    Ports.IMetadataCache? metadata = null)
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
    public async Task<SheetActivityPage> HandleAsync(
        long documentId, int sheetDefId, int periodKey, int? byUserId, bool includeSystem, int limit, string? cursor,
        CancellationToken ct)
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

        return await query
            .ReadAsync(
                new SheetActivityScope(documentId, periodKey, sheetCode, columns, byUserId, includeSystem, tables, owners),
                size, cursor, ct)
            .ConfigureAwait(false);
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
