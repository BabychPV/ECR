// src/Ecr.Application/Ports/ISqlCapabilities.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;

/// <summary>
/// Можливості СУБД, визначені при старті (АРХ-7). Редакція впливає <b>лише</b>
/// на операційні стратегії — ніколи на модель даних, семантику чи числа.
/// Тому в бізнес-коді звертатися сюди заборонено: тільки обслуговування
/// індексів, планувальник і <c>ArchiveJob</c>.
/// </summary>
public interface ISqlCapabilities
{
    SqlEditionMode EffectiveMode { get; }
    string EditionName { get; }
    int ProductMajorVersion { get; }
    bool IsReadCommittedSnapshotOn { get; }

    /// <summary>Перебудова індексів без блокування (<c>ONLINE = ON</c>).</summary>
    bool SupportsOnlineIndexRebuild { get; }

    /// <summary>Resource Governor для ізоляції фонових задач від інтерактивного піку.</summary>
    bool SupportsResourceGovernor { get; }

    /// <summary>Розмір батча архівації, підібраний під редакцію.</summary>
    int ArchiveBatchSize { get; }
}
