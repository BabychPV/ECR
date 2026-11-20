using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>Версія методології, що імпортує бібліотеку.</summary>
/// <param name="VersionId">Версія-імпортер.</param>
/// <param name="MethodologyId">Методологія-імпортер.</param>
/// <param name="MethodologyCode">Код методології-імпортера.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="Engine">Рушій версії.</param>
/// <param name="Status">Стан версії.</param>
public sealed record ImporterVersion(
    int VersionId,
    int MethodologyId,
    string MethodologyCode,
    string Version,
    MethodologyEngine Engine,
    TemplateVersionStatus Status);

/// <summary>Читання для перемикання методологій на скрипт (MS, план 8.5), що не лягають в інші порти.</summary>
public interface IScriptMigrationStore
{
    /// <summary>
    /// Усі версії, що імпортують названу методологію-бібліотеку (<c>calc.MethodologyImport</c>), у порядку коду й версії.
    /// </summary>
    /// <param name="libraryMethodologyId">Методологія-бібліотека.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ImporterVersion>> GetImporterVersionsAsync(int libraryMethodologyId, CancellationToken ct);

    /// <summary>
    /// Дати всередині життя версії (<c>EffectiveFrom &lt; дата &lt; наступна версія тієї ж методології</c>), на які
    /// набуває чинності будь-яка версія методології-бібліотеки: на цих межах версія бібліотеки, яку рантайм розв'язує для
    /// періоду версії, може змінитися. Дати чинності самої версії тут немає; порожньо — у версії немає дати чинності.
    /// </summary>
    /// <param name="versionId">Версія (імпортер).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<DateOnly>> GetLibraryEditionDatesAsync(int versionId, CancellationToken ct);
}
