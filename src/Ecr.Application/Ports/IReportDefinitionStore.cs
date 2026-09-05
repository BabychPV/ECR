// src/Ecr.Application/Ports/IReportDefinitionStore.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Описи звітів (<c>rpt.ReportDef</c>) та їхні версії.
/// </summary>
/// <remarks>
/// ⚠ Окремий порт від <see cref="IReportSnapshotBuilder"/>: той будує і читає
/// <b>зрізи</b>, а тут — <b>опис звіту</b>. Змішати їх означало б, що
/// побудова зрізу вміє змінювати визначення звіту, за яким її ж і будують.
/// </remarks>
public interface IReportDefinitionStore
{
    /// <summary>
    /// Чинна опублікована версія звіту за його кодом; <c>null</c> — звіту
    /// немає або жодну версію не опубліковано.
    /// </summary>
    /// <param name="code">Код звіту.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Чернетка версії зрізу не дає. Зріз, побудований за неопублікованим
    /// описом, виглядав би як звичайний і потрапив би в регуляторну вʼюху —
    /// а описи змінюють саме тоді, коли ще не впевнені в них.
    /// </remarks>
    public Task<int?> FindCurrentVersionIdAsync(string code, CancellationToken ct);
}
