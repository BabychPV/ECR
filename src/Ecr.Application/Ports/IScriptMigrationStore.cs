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
}
