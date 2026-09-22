// src/Ecr.Application/Integration/SourceCatalogHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration;

/// <summary>Позиція каталогу джерела: елемент або атрибут.</summary>
/// <param name="Code">Ім'я в джерелі.</param>
/// <param name="DisplayName">Опис із джерела.</param>
/// <param name="Path">Шлях в ієрархії AF.</param>
/// <param name="Kind"><c>Element</c> або <c>Attribute</c>.</param>
/// <param name="DataType">Тип значення в термінах джерела.</param>
/// <param name="UnitSymbol">UOM джерела; <c>null</c> — джерело одиниці не назвало.</param>
public sealed record SourceCatalogItem(
    string Code, string? DisplayName, string? Path, string Kind, string? DataType, string? UnitSymbol);

/// <summary>Сторінка каталогу; <c>NextCursor</c> <c>null</c> — сторінка остання.</summary>
public sealed record SourceCatalogPage(IReadOnlyList<SourceCatalogItem> Items, string? NextCursor);

/// <summary>Межа очікування каталогу зовнішнього джерела.</summary>
/// <param name="Timeout">Скільки чекати джерело, перш ніж відповісти 503.</param>
public sealed record SourceCatalogPolicy(TimeSpan Timeout)
{
    /// <summary>Типова межа, секунд (<c>Integration:CatalogTimeoutSeconds</c>).</summary>
    public const int DefaultTimeoutSeconds = 10;
}

/// <summary>
/// Перегляд каталогу джерела для мапінгу (ФВ-13.13). Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Власна коротка межа очікування: людина чекає перед екраном, а таймаут
/// транспорту (30 с × до трьох спроб) розрахований на фоновий збір.
/// </remarks>
public sealed class BrowseSourceCatalogHandler(
    IDataSourceStore store,
    ISourceCatalogReader reader,
    SourceCatalogPolicy policy,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Типовий розмір сторінки.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Стеля розміру сторінки.</summary>
    public const int MaxLimit = 200;

    /// <summary>Стеля довжини рядка пошуку.</summary>
    public const int MaxSearchLength = 200;

    /// <summary>
    /// Без <paramref name="path"/> — кореневі елементи; зі шляхом — дочірні
    /// елементи, а за ними атрибути цього елемента.
    /// </summary>
    public async Task<SourceCatalogPage> HandleAsync(
        int id, string? path, string? search, string? cursor, int? limit, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct).ConfigureAwait(false);

        var size = limit ?? DefaultLimit;
        var offset = 0;
        var term = search?.Trim();

        if (size is < 1 or > MaxLimit
            || term?.Length > MaxSearchLength
            || (cursor is not null
                && !(int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset >= 0)))
        {
            throw ListDataSourcesHandler.Invalid(
                "err.ECR-REQ-0422.catalogQueryInvalid",
                $"Сторінка каталогу — від 1 до {MaxLimit} позицій, пошук до {MaxSearchLength} символів, курсор із попередньої відповіді.");
        }

        var source = await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        var items = await ReadAsync(source, string.IsNullOrWhiteSpace(path) ? null : path, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(term))
        {
            items = [.. items.Where(i => i.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
                                         || (i.DisplayName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))];
        }

        var page = items.Skip(offset).Take(size).ToList();
        var next = offset + page.Count < items.Count
            ? (offset + page.Count).ToString(CultureInfo.InvariantCulture)
            : null;

        return new SourceCatalogPage(page, next);
    }

    private async Task<List<SourceCatalogItem>> ReadAsync(DataSource source, string? path, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(policy.Timeout);

        try
        {
            var found = new List<SourceEntityDescriptor>(
                await reader.BrowseAsync(source.Id, path, bounded.Token).ConfigureAwait(false));

            if (path is not null)
            {
                found.AddRange(await reader.AttributesAsync(source.Id, path, bounded.Token).ConfigureAwait(false));
            }

            return [.. found.Select(d => new SourceCatalogItem(
                d.Code, d.DisplayName, d.EntityPath, d.DataType is "Element" ? "Element" : "Attribute",
                d.DataType, d.SourceUnitSymbol))];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unavailable(source, "err.ECR-INT-0503.catalogTimeout", "не відповіло вчасно");
        }

        // ⚠ Відмова в автентифікації (ECR-INT-0502) і решта кодованих відмов
        // проходять як є; сюди — «джерело лежить» від транспорту.
        catch (Exception e) when (e is not EcrException and not OperationCanceledException
                                  || e is BusinessRuleException { ErrorCode: ErrorCodes.SourceUnavailable })
        {
            throw Unavailable(source, "err.ECR-INT-0503.catalogUnavailable", "недоступне");
        }
    }

    private BusinessRuleException Unavailable(DataSource source, string messageKey, string what)
        => new(
            ErrorCodes.SourceUnavailable,
            $"Джерело «{source.Code}» {what}: каталог не прочитано.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                ["code"] = source.Code,
                ["timeoutSeconds"] = (int)policy.Timeout.TotalSeconds,
            });
}
