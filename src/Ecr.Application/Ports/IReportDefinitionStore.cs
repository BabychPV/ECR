// src/Ecr.Application/Ports/IReportDefinitionStore.cs
using Ecr.Domain.ValueObjects;

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

    /// <summary>
    /// Усі описи звітів разом із їхніми версіями.
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Віддає і НЕактивні описи, і чернеткові версії — це перелік для того,
    /// хто описи ЗАВОДИТЬ. Сховати виведений з обігу звіт означало б, що
    /// причину, чому зріз за ним більше не будується, ніде не видно; сховати
    /// чернетку — що її нема як опублікувати. Той, хто лише будує зріз,
    /// фільтрує сам: побудова однаково бере лише опубліковане
    /// (<see cref="FindCurrentVersionIdAsync"/>).
    /// </remarks>
    public Task<IReadOnlyList<ReportDefinitionDto>> ListAsync(CancellationToken ct);

    /// <summary>Чи вже є опис звіту з таким кодом.</summary>
    /// <param name="code">Код звіту.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// Перевіряється ДО вставки: <c>UQ_ReportDef</c> відхилив би дублікат і
    /// сам, але вже винятком провайдера, який доїхав би клієнтові як
    /// <c>500</c> замість «такий звіт уже є».
    /// </remarks>
    public Task<bool> ExistsAsync(string code, CancellationToken ct);
}

/// <summary>Опис звіту разом із його версіями.</summary>
/// <param name="Id">Ідентифікатор опису.</param>
/// <param name="Code">Код звіту — саме ним будується зріз.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="IsRegulatory">
/// Чи звіт іде регулятору. Не позначка «важливий»: від неї залежить, який
/// фільтр статусів піде у вʼюху <c>rpt.v_*</c> (<c>D-65</c>).
/// </param>
/// <param name="IsActive">Чи звіт в обігу.</param>
/// <param name="Versions">Версії, найновіша першою.</param>
public sealed record ReportDefinitionDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    bool IsRegulatory,
    bool IsActive,
    IReadOnlyList<ReportVersionDto> Versions);

/// <summary>Версія опису звіту.</summary>
/// <param name="Id">Ідентифікатор версії.</param>
/// <param name="Version">Номер версії; унікальний у межах опису.</param>
/// <param name="Status">
/// <c>Draft</c> або <c>Published</c>. Зріз будується ЛИШЕ за опублікованою
/// (<see cref="IReportDefinitionStore.FindCurrentVersionIdAsync"/>).
/// </param>
/// <param name="ColumnsJson">Опис колонок зрізу.</param>
/// <param name="RulesJson">Правила відбору рядків.</param>
/// <param name="CreatedAt">Коли створено.</param>
public sealed record ReportVersionDto(
    int Id,
    string Version,
    string Status,
    string ColumnsJson,
    string RulesJson,
    DateTime CreatedAt);
