// src/Ecr.Application/Ports/ISqlCapabilities.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Можливості СУБД, визначені при старті (АРХ-7). Редакція впливає <b>лише</b>
/// на операційні стратегії — ніколи на модель даних, семантику чи числа.
/// Тому в бізнес-коді звертатися сюди заборонено: тільки обслуговування
/// індексів, планувальник і <c>ArchiveJob</c>.
/// </summary>
public interface ISqlCapabilities
{
    public SqlEditionMode EffectiveMode { get; }
    public string EditionName { get; }
    public int ProductMajorVersion { get; }
    public bool IsReadCommittedSnapshotOn { get; }

    /// <summary>Перебудова індексів без блокування (<c>ONLINE = ON</c>).</summary>
    public bool SupportsOnlineIndexRebuild { get; }

    /// <summary>Resource Governor для ізоляції фонових задач від інтерактивного піку.</summary>
    public bool SupportsResourceGovernor { get; }

    /// <summary>Розмір батча архівації, підібраний під редакцію.</summary>
    public int ArchiveBatchSize { get; }
}
