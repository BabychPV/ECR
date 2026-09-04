using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Offsets переходів періоду. Налаштовуються адміністратором (ФВ-1.6).</summary>
public sealed class PeriodPolicy : Entity<int>
{
    private PeriodPolicy() { }

    public PeriodPolicy(EcrCode code, int openOffsetDays, int graceOffsetDays,
                        int hardCloseOffsetDays, int yearGraceOffsetDays)
    {
        Code = code.Value;
        OpenOffsetDays = openOffsetDays;
        GraceOffsetDays = graceOffsetDays;
        HardCloseOffsetDays = hardCloseOffsetDays;
        YearGraceOffsetDays = yearGraceOffsetDays;
    }

    public string Code { get; private set; } = null!;

    /// <summary>Коли період відкривається, від його **початку**. Може бути від'ємним.</summary>
    public int OpenOffsetDays { get; private set; }

    /// <summary>Скільки днів після завершення періоду редагування ще дозволене.</summary>
    public int GraceOffsetDays { get; private set; }

    /// <summary>Коли період закривається остаточно.</summary>
    public int HardCloseOffsetDays { get; private set; }

    /// <summary>Скільки днів після завершення **року** дані ще редагуються.</summary>
    public int YearGraceOffsetDays { get; private set; }
}
