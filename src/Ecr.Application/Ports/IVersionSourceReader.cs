using Ecr.Application.Calculations.Scripting.Migration;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання СТАРОГО вмісту версії методології (формули, константи, правила, виходи, речовини, імпорти) в моделі
/// <see cref="VersionSource"/> — з того ж боку, що й відбиток джерела (MS, план 8.1, 8.5).
/// </summary>
/// <remarks>
/// ⛔ Лише читання: нічого не відстежується й не пишеться. Той самий код читає інструмент перенесення і
/// <c>POST …/script-migration/apply</c>, тому відбитки обох збігаються за побудовою.
/// </remarks>
public interface IVersionSourceReader
{
    /// <summary>Читає версію з бази.</summary>
    /// <param name="versionId">Id версії.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="InvalidOperationException">Версії немає.</exception>
    public Task<LoadedVersion> LoadAsync(int versionId, CancellationToken ct);
}
