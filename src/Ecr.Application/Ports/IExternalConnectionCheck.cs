// src/Ecr.Application/Ports/IExternalConnectionCheck.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Перевірка самого з'єднання з джерелом — без каталогу сутностей (b14 source-test-flert, AC-2).
/// </summary>
/// <remarks>
/// ⚠ Проба з'єднання й далі йде каталогом (<see cref="IExternalDataSource.DiscoverAsync"/>) — тією самою дорогою,
/// що й збір. Цей порт — запасна дорога лише для джерела, у якого запиту каталогу НЕ налаштовано
/// (<c>SourceNotConfiguredException.CatalogOnly</c>): FLERT для складу газу (<c>D-476</c>) читається власним
/// запитом, каталогу не має, і проба доти завжди казала «недоступне», навіть коли з'єднання ціле.
/// <para>
/// ⛔ Відмова — виняток того самого виду, що й у <see cref="IExternalDataSource.DiscoverAsync"/> (автентифікація,
/// недоступність, налаштування), щоб проба класифікувала її так само.
/// </para>
/// </remarks>
public interface IExternalConnectionCheck
{
    /// <summary>Відкриває з'єднання з джерелом і виконує найпростіший запит (<c>SELECT 1</c>).</summary>
    /// <param name="dataSourceId">Джерело.</param>
    /// <param name="ct">Скасування.</param>
    public Task CheckConnectionAsync(int dataSourceId, CancellationToken ct);
}
