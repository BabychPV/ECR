// tests/Ecr.Application.Tests/Notifications/TransactionProbe.cs
using Ecr.Application.Ports;
using NSubstitute;

namespace Ecr.Application.Tests.Notifications;

/// <summary>
/// S8: підміна <see cref="IUnitOfWork"/>, що ВИКОНУЄ замикання транзакції і запам'ятовує, чи подія аудиту та
/// <c>SaveChangesAsync</c> відбулися всередині нього (поза ним подія автокомітилась би окремо від зміни).
/// </summary>
internal sealed class TransactionProbe
{
    private bool _inside;

    /// <summary>Для кожного запису аудиту: чи він усередині транзакції.</summary>
    public List<bool> AuditInside { get; } = [];

    /// <summary>Для кожного <c>SaveChangesAsync</c>: чи він усередині транзакції.</summary>
    public List<bool> SaveInside { get; } = [];

    /// <summary>Підключає пробу до підмін.</summary>
    public static TransactionProbe Attach(IUnitOfWork uow, IAuditWriter audit)
    {
        var probe = new TransactionProbe();

        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => probe.RunAsync(call.Arg<Func<CancellationToken, Task>>(), call.Arg<CancellationToken>()));
        uow.When(u => u.SaveChangesAsync(Arg.Any<CancellationToken>())).Do(_ => probe.SaveInside.Add(probe._inside));
        audit.When(a => a.WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>()))
            .Do(_ => probe.AuditInside.Add(probe._inside));

        return probe;
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        _inside = true;

        try
        {
            await operation(ct).ConfigureAwait(false);
        }
        finally
        {
            _inside = false;
        }
    }
}