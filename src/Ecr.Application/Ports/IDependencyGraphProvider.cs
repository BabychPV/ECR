// src/Ecr.Application/Ports/IDependencyGraphProvider.cs
using Ecr.Application.DocumentDependencies;

namespace Ecr.Application.Ports;

/// <summary>
/// B179 L1: граф залежностей документів рівня шаблонів — з того, що вже пише публікація
/// (<c>cfg.FormulaDependency</c> вид 2 + AST-запасний шлях, <c>cfg.RegistryUse</c> види 1/2,
/// <c>cfg.CalculationBinding</c>, <c>cfg.DocumentSourceBinding</c>); жодного ручного переліку споживачів.
/// </summary>
/// <remarks>
/// Кешується в спільному <c>IMemoryCache</c> під ключем ревізії вхідних даних (прийом D-16 кешу метаданих): публікація
/// шаблону чи методології, зміна прив'язки, зв'язки чи визначення довідника змінюють ревізію — новий ключ, без явної
/// інвалідації й однаково на всіх вузлах. Стеля несвіжості — вікно ревізії (<c>CacheLifetimes.Revision</c>, 5 с).
/// </remarks>
public interface IDependencyGraphProvider
{
    /// <summary>Поточний граф.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<DependencyGraphSnapshot> GetAsync(CancellationToken ct);

    /// <summary>
    /// Граф за ревізією, прочитаною з бази ЗАРАЗ (без вікна мемоїзації ревізії): для перевірок усередині транзакції, що
    /// щойно записала ребра (MS x B179: відмова публікації методології, що замкнула цикл) — звичайний <see cref="GetAsync"/>
    /// до 5 с віддавав би граф до запису.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<DependencyGraphSnapshot> GetFreshAsync(CancellationToken ct);

    /// <summary>
    /// B179 L3: чи наповнює шаблон хоч один довідник (<c>cfg.DocumentSourceBinding</c>) — один запит без побудови графа.
    /// Шаблон без зв'язки не має вихідних ребер, тож цикл через нього неможливий.
    /// </summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<bool> FillsRegistryAsync(int templateId, CancellationToken ct);
}
