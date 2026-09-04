// src/Ecr.Application/Periods/ReopenPeriodHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Application.Common;

namespace Ecr.Application.Periods;

/// <summary>
/// Адміністративне відкриття закритого періоду (ФВ-1.10). Право
/// <c>Period.Reopen</c> — небезпечне, у складені ролі не входить (ФВ-6.12).
/// </summary>
public sealed class ReopenPeriodHandler(
    IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task HandleAsync(int periodId, string reason, DateTime? until, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) взяти doc.Period з UPDLOCK і перечитати стан У ТРАНЗАКЦІЇ — " +
            "   інакше PeriodStateJob може закрити період посеред операції (ФВ-1.10a);\n" +
            "2) Archived НЕ відкривається: це кінцевий стан (ФВ-1.10);\n" +
            "3) reason обов'язковий; until — необов'язковий, за замовчуванням " +
            "   до кінця доби в поясі майданчика;\n" +
            "4) Closed → Grace, а не → Open: правки після закриття лишаються " +
            "   пізніми і мають позначатися IsLateEdit (D-70);\n" +
            "5) аудит із причиною.");
}
