// src/Ecr.Application/Templates/SearchColumnDefsHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates;

/// <summary>
/// Пошук колонок за назвою чи кодом, поза межами однієї таблиці (директива
/// "пошук колонки за назвою замість голого ColumnDefId").
/// </summary>
/// <remarks>
/// ⚠ Право читання те саме, що й у <see cref="GetTemplateStructureHandler"/>
/// (<c>Template.View</c>): результат несе ті самі відомості — коди й назви
/// колонок, таблиць, аркушів, — що вже віддає <c>GET …/structure</c>, лише
/// наскрізь по всіх версіях одразу.
/// </remarks>
public sealed class SearchColumnDefsHandler(
    IColumnDefSearchStore store,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на пошук (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.View";

    /// <summary>Виконує пошук.</summary>
    /// <param name="query"><c>null</c> або порожній — без фільтра.</param>
    /// <param name="limit">Стеля кількості результатів; <c>&lt;= 0</c> — типове значення.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<ColumnDefSearchResultDto>> HandleAsync(
        string? query, int limit, CancellationToken ct)
    {
        await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var found = await store.SearchAsync(query, limit, ct).ConfigureAwait(false);

        return [.. found.Select(r => new ColumnDefSearchResultDto(
            r.Id, r.Code, r.HeaderL10n, r.TableDefId, r.TableCode, r.SheetDefId, r.SheetCode,
            r.TemplateVersionId))];
    }
}

/// <summary>Одна знахідка пошуку колонки, у формі відповіді API.</summary>
/// <param name="Id">Ідентифікатор колонки — те саме значення, що йде в <c>ColumnDefId</c>.</param>
/// <param name="Code">Код колонки; унікальний у межах таблиці, не глобально.</param>
/// <param name="HeaderL10n">Заголовок колонки мовами каталогу.</param>
/// <param name="TableDefId">Таблиця колонки.</param>
/// <param name="TableCode">Код таблиці — для підпису в списку вибору.</param>
/// <param name="SheetDefId">Аркуш таблиці.</param>
/// <param name="SheetCode">Код аркуша — для підпису в списку вибору.</param>
/// <param name="TemplateVersionId">Версія шаблону, якій належить аркуш.</param>
public sealed record ColumnDefSearchResultDto(
    int Id,
    string Code,
    LocalizedText HeaderL10n,
    int TableDefId,
    string TableCode,
    int SheetDefId,
    string SheetCode,
    int TemplateVersionId);
