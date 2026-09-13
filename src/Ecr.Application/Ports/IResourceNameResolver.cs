// src/Ecr.Application/Ports/IResourceNameResolver.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Розв'язує людяний код ресурсу гранта за видом і числовим ідентифікатором
/// (<c>Q-299</c>), БЕЗ підказки версії шаблону.
/// </summary>
/// <remarks>
/// ⚠ Розв'язання за <c>(kind, id)</c> без версії коректне лише тому, що
/// <c>SheetDef.Id</c>/<c>TableDef.Id</c>/<c>ColumnDef.Id</c> — суцільні
/// IDENTITY-ключі власних таблиць (<c>cfg.SheetDef</c>/<c>cfg.TableDef</c>/
/// <c>cfg.ColumnDef</c>), а НЕ складові ключі з <c>TemplateVersionId</c>:
/// <c>TemplateStructureConfiguration.cs</c> оголошує лише
/// <c>HasKey(x => x.Id)</c>, а <c>(TemplateVersionId, Code)</c> — це
/// ОКРЕМИЙ унікальний індекс для коду, не первинний ключ. Той самий числовий
/// <c>Id</c> тому не повторюється у двох версіях одночасно — якби повторювався,
/// цей порт без версії був би недовизначений.
/// </remarks>
public interface IResourceNameResolver
{
    /// <summary>
    /// Повертає код ресурсу для кожної пари <c>(kind, id)</c>, яку вдалося
    /// знайти. Пара, якої немає в результаті, — ресурс видалено фізично або
    /// посилання «осиротіло»; виклик цього не вважає помилкою.
    /// </summary>
    /// <param name="resources">Пари вид/ідентифікатор; дублікати допустимі.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<(ResourceKind Kind, int Id), string>> ResolveAsync(
        IReadOnlyCollection<(ResourceKind Kind, int Id)> resources, CancellationToken ct);
}
