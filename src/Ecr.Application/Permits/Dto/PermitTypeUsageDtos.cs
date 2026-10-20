// src/Ecr.Application/Permits/Dto/PermitTypeUsageDtos.cs
namespace Ecr.Application.Permits.Dto;

/// <summary>Скільки даних лежить у ОПУБЛІКОВАНІЙ таблиці типу дозволу (Permits, PM-03b2).</summary>
/// <param name="Code">Код таблиці в межах типу.</param>
/// <param name="RowCount">Скільки живих рядків у цій таблиці в усіх дозволах типу.</param>
public sealed record PermitTypeTableUsageDto(string Code, int RowCount);

/// <summary>
/// Використання типу дозволу даними: скільки дозволів (екземплярів) і скільки рядків у кожній опублікованій таблиці.
/// Лише кількості - жодних значень чи ідентифікаторів записів.
/// </summary>
/// <param name="InstanceCount">Скільки живих дозволів цього типу (чернетки теж: їхні дані втратилися б так само).</param>
/// <param name="Tables">Опубліковані таблиці з кількістю рядків; таблиць, що лише в чернетці, тут немає.</param>
public sealed record PermitTypeUsageDto(int InstanceCount, IReadOnlyList<PermitTypeTableUsageDto> Tables);
