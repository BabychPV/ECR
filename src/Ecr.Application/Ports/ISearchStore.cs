// src/Ecr.Application/Ports/ISearchStore.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>Що саме дозволено шукати поточному користувачу (BE-19).</summary>
/// <param name="DocumentProjects">Проєкти, документи яких видно; <c>null</c> — документи не шукати.</param>
/// <param name="Templates">Чи шукати шаблони.</param>
/// <param name="Registries">Чи шукати довідники.</param>
public sealed record SearchScope(IReadOnlyCollection<int>? DocumentProjects, bool Templates, bool Registries);

/// <summary>Сирий збіг пошуку до локалізації назви.</summary>
/// <param name="Kind">Тип сутності: <c>document</c>, <c>template</c>, <c>registry</c>.</param>
/// <param name="Id">Ідентифікатор сутності.</param>
/// <param name="Code">Код (для документа — <c>BusinessKey</c>).</param>
/// <param name="Name">Назва мовами каталогу; <c>null</c> — не задано.</param>
public sealed record SearchRow(string Kind, long Id, string Code, LocalizedText? Name);

/// <summary>Пошук даних для командної палітри за підрядком коду чи назви.</summary>
public interface ISearchStore
{
    /// <summary>Шукає в межах <paramref name="scope"/>; кожен тип — не більше <paramref name="perKind"/>.</summary>
    /// <param name="term">Підрядок, уже обрізаний і не коротший за мінімум.</param>
    /// <param name="scope">Межа видимості.</param>
    /// <param name="perKind">Стеля збігів на один тип сутності.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<SearchRow>> SearchAsync(
        string term, SearchScope scope, int perKind, CancellationToken ct);
}
