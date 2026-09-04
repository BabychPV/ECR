using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Реалізація <see cref="IAccessDecisionService"/>: поєднує RBAC, стан періоду,
/// правила періодів шаблону, статус документа і структурні обмеження.
/// </summary>
/// <remarks>
/// Повертає **причину**, а не <c>bool</c>: користувач має розуміти, чому
/// комірка сіра, інакше він піде до адміністратора, а той — до розробника.
/// </remarks>
public sealed class AccessDecisionService(
    EcrDbContext db,
    IMetadataCache metadata,
    Caching.AccessProfileCache profileCache) : IAccessDecisionService
{
    /// <inheritdoc />
    public Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: зібрати ролі користувача, їхні функціональні права і ресурсні гранти; " +
            "РОЗГОРНУТИ успадкування Project → Sheet → Table → Column у плоску мапу; " +
            "окремо зібрати заборони (IsDeny) — вони виграють на будь-якому рівні (ФВ-6.6); " +
            "ключ кешу = userId + securityStamp.");

    /// <inheritdoc />
    public Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct)
        => throw new NotImplementedException("TODO: рівень гранта на проєкт документа має бути >= Read.");

    /// <inheritdoc />
    public Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — порядок перевірок від найдешевшої до найдорожчої, повертати ПЕРШУ причину:\n" +
            "1) Project.Status == Archived → ProjectArchived;\n" +
            "2) Project.IsArchiving → ArchivingInProgress;\n" +
            "3) Period.State: Scheduled → PeriodNotOpenYet, Closed → PeriodClosed;\n" +
            "   ⚠ закритий період блокує ВСІХ, включно з Manage (02c A7);\n" +
            "4) PeriodAccessRuleDef для аркуша й номера періоду → OutOfAccessWindow;\n" +
            "5) ApprovalState аркуша: Submitted → DocumentSubmitted, Approved → DocumentApproved;\n" +
            "   ⚠ Grace дає час на правки НЕПОДАНИХ документів, а не право змінити подану форму (D-67);\n" +
            "6) ColumnDef.IsComputed → CalculatedCell; IsReadOnly → ColumnReadOnly;\n" +
            "7) RowDef.IsReadOnly → RowReadOnly;\n" +
            "8) profile.LevelFor(Column|Table|Sheet|Project) < Write → NoGrant;\n" +
            "⚠ Project.CurrentPeriod у цьому ланцюгу НЕ бере участі: інакше «пін» став би " +
            "прихованим правом редагувати закрите (D-77).");

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: обчислити спільні для зрізу умови ОДИН раз (проєкт, період, правила періодів, " +
            "статус аркуша, грант на таблицю), далі пройтися по колонках і рядках у пам'яті. " +
            "Поштучний виклик CanEditCellAsync у циклі — антипатерн: він не вкладається в бюджет.");

    /// <inheritdoc />
    public Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => throw new NotImplementedException("TODO: рівень >= Submit; немає незакритих Error валідації.");

    /// <inheritdoc />
    public Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => throw new NotImplementedException("TODO: рівень >= Approve; стан аркуша = Submitted.");
}
