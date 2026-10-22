// src/Ecr.Application/Ports/IPermitGroupStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>Доступ до груп типів дозволів і їхнього складу (Permits, PRMG).</summary>
/// <remarks>
/// ⚠ Окремий порт: ядро реєстрів про групи не знає. Груп десятки - вибірки без сторінок.
/// </remarks>
public interface IPermitGroupStore
{
    /// <summary>Усі групи зі складом (без відстеження), за кодом.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitGroup>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Короткі відомості про всі групи одним запитом без складу (перелік довідників: приховати <c>PRMG_*</c> із «Довідників» і
    /// показати активні як цілі Lookup з описом).
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitGroupBrief>> ListBriefAsync(CancellationToken ct);

    /// <summary>Група зі складом за кодом (відстежувана); <c>null</c> - немає.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PermitGroup?> FindByCodeAsync(string code, CancellationToken ct);

    /// <summary>Ставить групу в чергу на вставку.</summary>
    /// <param name="group">Група.</param>
    public void Add(PermitGroup group);

    /// <summary>
    /// Позначає видаленими рядки складу, яких немає в <paramref name="keepPermitTypeDefIds"/>. ⛔ Кликати ДО
    /// <c>PermitGroup.ReplaceMembers</c>: усі FK - Restrict, і рядок, прибраний з колекції раніше за позначку, EF вважає
    /// розірваним обовʼязковим зв'язком.
    /// </summary>
    /// <param name="group">Група (відстежувана, зі складом).</param>
    /// <param name="keepPermitTypeDefIds">Типи, що лишаються у складі.</param>
    public void RemoveMembersNotIn(PermitGroup group, IReadOnlyCollection<int> keepPermitTypeDefIds);

    /// <summary>Прибирає групу разом зі складом і прихованим реєстром-«ідентичністю».</summary>
    /// <param name="group">Група.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RemoveAsync(PermitGroup group, CancellationToken ct);

    /// <summary>
    /// Знімок для <see cref="Permits.Groups.IPermitGroupResolver"/>: по рядку на групу з реєстрами шапок типів складу.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<PermitGroupSnapshot> LoadSnapshotAsync(CancellationToken ct);

    /// <summary>
    /// Записи (дозволи) реєстрів шапок складу групи ОДНИМ запитом <c>WHERE RegistryDefId IN (…)</c>, без видалених і без
    /// відстеження; чинність і стан відбирає <c>RegistryResolver</c>.
    /// </summary>
    /// <param name="registryDefIds">Реєстри шапок (вже відфільтровані за правами читача).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<Ecr.Domain.Entities.Dictionaries.RegistryEntry>> ListMemberEntriesAsync(
        IReadOnlyCollection<int> registryDefIds, CancellationToken ct);

    /// <summary><c>DataRevision</c> кожного з реєстрів одним запитом (частина ключа кешу піклера групи).</summary>
    /// <param name="registryDefIds">Реєстри.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<int, int>> GetDataRevisionsAsync(IReadOnlyCollection<int> registryDefIds, CancellationToken ct);

    /// <summary>
    /// Дешева «ревізія» складу всіх груп (сума <c>DataRevision + 1</c> прихованих реєстрів груп):
    /// змінюється, коли будь-яку групу створили, видалили чи змінили склад. Один агрегатний запит.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<long> GetSnapshotRevisionAsync(CancellationToken ct);
}

/// <summary>Короткі відомості про групу (без складу).</summary>
/// <param name="RegistryDefId">Прихований реєстр-«ідентичність» групи.</param>
/// <param name="IsActive">Чи група активна.</param>
/// <param name="DescriptionL10n">Опис групи.</param>
public sealed record PermitGroupBrief(int RegistryDefId, bool IsActive, Ecr.Domain.ValueObjects.LocalizedText DescriptionL10n);

/// <summary>Група у знімку резолвера.</summary>
/// <param name="GroupRegistryDefId">Прихований реєстр-«ідентичність» групи (ціль Lookup).</param>
/// <param name="IsActive">Чи група активна.</param>
/// <param name="MemberRegistryDefIds">Реєстри шапок типів складу в порядку складу.</param>
public sealed record PermitGroupSnapshotItem(int GroupRegistryDefId, bool IsActive, IReadOnlyList<int> MemberRegistryDefIds);

/// <summary>Знімок усіх груп разом із ревізією, за якою він знятий.</summary>
/// <param name="Revision">Значення <see cref="IPermitGroupStore.GetSnapshotRevisionAsync"/> на момент читання.</param>
/// <param name="Items">Групи.</param>
public sealed record PermitGroupSnapshot(long Revision, IReadOnlyList<PermitGroupSnapshotItem> Items);
