using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Умови доступу до комірки, зібрані в одному місці.
/// </summary>
/// <remarks>
/// ⚠ <c>Project.CurrentPeriod</c> сюди НЕ входить — навмисно. Якби він брав
/// участь у рішенні, «пін» поточного періоду став би прихованим правом
/// редагувати закрите (D-77). Поточний період — навігаційна зручність, а не
/// повноваження.
/// </remarks>
/// <param name="ProjectId">Проєкт документа.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="ProjectStatus">Стан проєкту.</param>
/// <param name="IsArchiving">Чи триває архівація проєкту.</param>
/// <param name="PeriodState">Стан періоду — збережене значення, не функція від <c>now()</c>.</param>
/// <param name="OutOfAccessWindow">Аркуш поза вікном доступу за номером періоду (ФВ-2.16).</param>
/// <param name="SheetStatus">Стан робочого процесу на аркуш × період.</param>
/// <param name="ColumnIsComputed">Колонка обчислювана.</param>
/// <param name="ColumnIsReadOnly">Колонка лише для читання.</param>
/// <param name="RowIsReadOnly">Рядок лише для читання.</param>
public readonly record struct CellAccessContext(
    int ProjectId,
    int SheetDefId,
    int TableDefId,
    int ColumnDefId,
    ProjectStatus ProjectStatus,
    bool IsArchiving,
    PeriodState PeriodState,
    bool OutOfAccessWindow,
    DocumentStatus SheetStatus,
    bool ColumnIsComputed,
    bool ColumnIsReadOnly,
    bool RowIsReadOnly);

/// <summary>
/// Правила доступу як **чиста функція**: жодних запитів, жодного часу.
/// </summary>
/// <remarks>
/// Винесені окремо від <c>IAccessDecisionService</c> не заради шарів. Рішення
/// про доступ — те, що найважче перевірити й найдорожче помилитися; чиста
/// функція дозволяє прогнати всі п'ятнадцять сценаріїв <c>02c §6</c> і
/// <c>tz/07</c> §7.6 за мілісекунди й без бази. Служба лишає собі те, що вміє
/// лише вона: дістати дані.
///
/// ⚠ Перевірка <c>ProjectStatus.Archived</c>/<c>IsArchiving</c> — спільна для
/// ВСІХ чотирьох рішень (<see cref="CanEdit"/>, <see cref="CanSubmit"/>,
/// <see cref="CanApprove"/>, <see cref="CanReopen"/>), одразу після
/// симуляції: архівація — термінальний стан проєкту, і робочий процес має
/// зупинятись так само, як і редагування (<c>tz/07</c> §7.4).
/// </remarks>
public static class EditRules
{
    /// <summary>Чи можна редагувати комірку.</summary>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="context">Умови конкретної комірки.</param>
    /// <returns>Рішення з <b>причиною</b> відмови.</returns>
    public static EditDecision CanEdit(AccessProfile profile, CellAccessContext context)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // ⚠ Симуляція відхиляє запис ПЕРШОЮ і незалежно від прав того, кого
        // симулюють (D-96). Інакше «подивитися очима» стало б способом
        // зробити зміну від чужого імені.
        if (profile.IsSimulation)
        {
            return EditDecision.Deny(
                EditDenyReason.SimulationReadOnly,
                $"Сеанс симуляції користувача {profile.SimulatedForUserId}.");
        }

        // Далі — від найдешевшої перевірки до найдорожчої, і повертається
        // ПЕРША причина: користувачеві потрібна одна зрозуміла відповідь, а не
        // перелік усього, що не так.
        if (context.ProjectStatus == ProjectStatus.Archived)
        {
            return EditDecision.Deny(EditDenyReason.ProjectArchived);
        }

        if (context.IsArchiving)
        {
            return EditDecision.Deny(EditDenyReason.ArchivingInProgress);
        }

        switch (context.PeriodState)
        {
            case PeriodState.Scheduled:
                return EditDecision.Deny(EditDenyReason.PeriodNotOpenYet);

            // ⚠ Закритий період блокує ВСІХ, включно з Manage (02c A7). Це
            // головна перевірка моделі доступу: якщо вона пропускає, зламана
            // вся модель, і жоден інший тест цього не покаже.
            case PeriodState.Closed:
                return EditDecision.Deny(EditDenyReason.PeriodClosed);

            default:
                break;
        }

        if (context.OutOfAccessWindow)
        {
            return EditDecision.Deny(EditDenyReason.OutOfAccessWindow);
        }

        // ⚠ Grace дає час на правки НЕПОДАНИХ документів, а не право змінити
        // подану форму (D-67). Тому стан аркуша перевіряється незалежно від
        // стану періоду.
        switch (context.SheetStatus)
        {
            case DocumentStatus.Submitted:
                return EditDecision.Deny(EditDenyReason.DocumentSubmitted);

            case DocumentStatus.Approved:
                return EditDecision.Deny(EditDenyReason.DocumentApproved);

            default:
                break;
        }

        if (context.ColumnIsComputed)
        {
            return EditDecision.Deny(EditDenyReason.CalculatedCell);
        }

        if (context.ColumnIsReadOnly)
        {
            return EditDecision.Deny(EditDenyReason.ColumnReadOnly);
        }

        if (context.RowIsReadOnly)
        {
            return EditDecision.Deny(EditDenyReason.RowReadOnly);
        }

        return Effective(profile, context) >= GrantLevel.Write
            ? EditDecision.Allow()
            : EditDecision.Deny(EditDenyReason.NoGrant);
    }

    /// <summary>Чи можна подати аркуш на погодження.</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="context">Умови аркуша.</param>
    /// <param name="hasBlockingErrors">Чи є незакриті помилки валідації.</param>
    public static EditDecision CanSubmit(AccessProfile profile, CellAccessContext context, bool hasBlockingErrors)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.IsSimulation)
        {
            return EditDecision.Deny(EditDenyReason.SimulationReadOnly);
        }

        if (context.ProjectStatus == ProjectStatus.Archived)
        {
            return EditDecision.Deny(EditDenyReason.ProjectArchived);
        }

        if (context.IsArchiving)
        {
            return EditDecision.Deny(EditDenyReason.ArchivingInProgress);
        }

        if (context.PeriodState == PeriodState.Closed)
        {
            return EditDecision.Deny(EditDenyReason.PeriodClosed);
        }

        if (context.SheetStatus is DocumentStatus.Submitted or DocumentStatus.Approved)
        {
            return EditDecision.Deny(
                context.SheetStatus == DocumentStatus.Submitted
                    ? EditDenyReason.DocumentSubmitted
                    : EditDenyReason.DocumentApproved);
        }

        // ⚠ Подання потребує рівня Submit, а не Write: право заповнювати і
        // право відповідати за подане — різні повноваження (02c A11).
        //
        // ⛔ Грант ВІДСУТНІЙ (None) і грант Є, але закороткий, — дві різні
        // причини відмовити, і до цього обидві поверталися як NoGrant.
        // Користувачеві з рівнем View/Write це читалося як «у вас немає
        // жодного доступу», хоча насправді доступ є — бракує саме рівня
        // Submit, і дія користувача інша: просити підвищення гранта, а не
        // грант із нуля.
        var effective = Effective(profile, context);
        if (effective < GrantLevel.Submit)
        {
            return effective == GrantLevel.None
                ? EditDecision.Deny(EditDenyReason.NoGrant)
                : EditDecision.Deny(
                    EditDenyReason.InsufficientGrantLevel,
                    $"Наявний рівень гранта — {effective}; для подання потрібен {GrantLevel.Submit}.");
        }

        return hasBlockingErrors
            ? EditDecision.Deny(EditDenyReason.BusinessRule, "Є незакриті помилки валідації.")
            : EditDecision.Allow();
    }

    /// <summary>Чи можна затвердити аркуш.</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="context">Умови аркуша.</param>
    /// <param name="requiredRoleId">
    /// Роль ПОТОЧНОГО кроку маршруту погодження (<c>ФВ-5.17</c>);
    /// <c>null</c> — маршруту немає, і затвердження одноетапне.
    /// </param>
    public static EditDecision CanApprove(
        AccessProfile profile, CellAccessContext context, int? requiredRoleId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.IsSimulation)
        {
            return EditDecision.Deny(EditDenyReason.SimulationReadOnly);
        }

        if (context.ProjectStatus == ProjectStatus.Archived)
        {
            return EditDecision.Deny(EditDenyReason.ProjectArchived);
        }

        if (context.IsArchiving)
        {
            return EditDecision.Deny(EditDenyReason.ArchivingInProgress);
        }

        // Затверджувати можна лише подане: затвердження чернетки означало б,
        // що ніхто не заявив її готовою.
        if (context.SheetStatus != DocumentStatus.Submitted)
        {
            return EditDecision.Deny(
                EditDenyReason.BusinessRule, $"Аркуш у стані {context.SheetStatus}, а не Submitted.");
        }

        // ⛔ Роль кроку перевіряється НА ДОДАЧУ до гранта, а не замість нього.
        // Маршрут каже «чия черга», грант — «чи має ця людина право на цей
        // проєкт узагалі»; замінити друге першим означало б, що додавання
        // ролі в маршрут роздає доступ до чужих проєктів.
        //
        // ⚠ Параметр необов'язковий, і за замовчуванням поведінка **не
        // змінюється**: маршрутів у seed немає, система без них працює як
        // раніше, і жоден наявний тест затвердження не правився.
        if (requiredRoleId is { } roleId && !profile.RoleIds.Contains(roleId))
        {
            return EditDecision.Deny(
                EditDenyReason.NoGrant,
                $"Крок маршруту погодження вимагає ролі {roleId}; зараз черга не ваша.");
        }

        // ⛔ Та сама різниця причин, що в CanSubmit вище: грант ВІДСУТНІЙ і
        // грант Є, але нижчий за Approve, — не одне й те саме для
        // користувача, який читає відмову.
        var effective = Effective(profile, context);
        if (effective < GrantLevel.Approve)
        {
            return effective == GrantLevel.None
                ? EditDecision.Deny(EditDenyReason.NoGrant)
                : EditDecision.Deny(
                    EditDenyReason.InsufficientGrantLevel,
                    $"Наявний рівень гранта — {effective}; для затвердження потрібен {GrantLevel.Approve}.");
        }

        return EditDecision.Allow();
    }

    /// <summary>Чи можна повернути поданий/затверджений аркуш у <c>Draft</c>.</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="context">Умови аркуша.</param>
    /// <remarks>
    /// ⛔ Q-173 (аудит фази 2, авторизація). Поріг — <see cref="GrantLevel.Approve"/>,
    /// не <see cref="GrantLevel.Write"/>: <c>ApprovalState.Reopen</c> скасовує і
    /// <c>Submitted</c>, і <c>Approved</c> — скасувати затвердження має право
    /// той, хто має право його дати, не менше.
    /// </remarks>
    public static EditDecision CanReopen(AccessProfile profile, CellAccessContext context)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.IsSimulation)
        {
            return EditDecision.Deny(EditDenyReason.SimulationReadOnly);
        }

        if (context.ProjectStatus == ProjectStatus.Archived)
        {
            return EditDecision.Deny(EditDenyReason.ProjectArchived);
        }

        if (context.IsArchiving)
        {
            return EditDecision.Deny(EditDenyReason.ArchivingInProgress);
        }

        if (context.SheetStatus is not (DocumentStatus.Submitted or DocumentStatus.Approved))
        {
            return EditDecision.Deny(
                EditDenyReason.BusinessRule, $"Аркуш у стані {context.SheetStatus}, а не Submitted/Approved.");
        }

        return Effective(profile, context) >= GrantLevel.Approve
            ? EditDecision.Allow()
            : EditDecision.Deny(EditDenyReason.NoGrant);
    }

    /// <summary>
    /// Ефективний рівень: найдрібніший оголошений рівень перемагає, заборона —
    /// завжди.
    /// </summary>
    /// <remarks>
    /// ⚠ Дві різні речі в одному місці:
    /// <list type="number">
    /// <item><b>Заборона виграє на будь-якому рівні</b> (ФВ-6.6). Deny на
    /// проєкті перекриває Manage на аркуші — це свідома жорсткість:
    /// альтернатива «конкретніший виграє» дає доступ, який ніхто не може
    /// пояснити.</item>
    /// <item><b>Дозвіл береться з найдрібнішого оголошеного рівня</b>: грант на
    /// колонку перекриває грант на таблицю. Саме так звужують права точково —
    /// інакше довелося б переоформлювати весь проєкт заради однієї колонки.</item>
    /// </list>
    /// </remarks>
    public static GrantLevel Effective(AccessProfile profile, CellAccessContext context)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var scopes = new (ResourceKind Kind, int Id)[]
        {
            (ResourceKind.Project, context.ProjectId),
            (ResourceKind.Sheet, context.SheetDefId),
            (ResourceKind.Table, context.TableDefId),
            (ResourceKind.Column, context.ColumnDefId),
        };

        foreach (var (kind, id) in scopes)
        {
            if (profile.Denies.Contains($"{kind}:{id}"))
            {
                return GrantLevel.None;
            }
        }

        // Від найдрібнішого до найширшого: перший оголошений і виграє.
        for (var i = scopes.Length - 1; i >= 0; i--)
        {
            var (kind, id) = scopes[i];
            if (profile.Grants.TryGetValue($"{kind}:{id}", out var level))
            {
                return level;
            }
        }

        return GrantLevel.None;
    }
}
