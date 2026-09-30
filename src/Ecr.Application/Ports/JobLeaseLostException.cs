// src/Ecr.Application/Ports/JobLeaseLostException.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Оренду задачі черги втрачено: її вже перехопив інший виконавець (MI-02, <c>D-208</c>).
/// </summary>
/// <remarks>
/// ⛔ Кидається зсередини транзакції видимості (<see cref="IJobQueue.FenceAsync"/>
/// повернув <c>false</c>), щоб відкотити її: результат, який виконавець без
/// оренди встиг порахувати, НЕ стає видимим — видимим стане результат
/// виконавця, що тримає оренду зараз.
/// <para>
/// ⚠ Не <c>EcrException</c> і без коду каталогу: до HTTP-відповіді він не
/// доходить ніколи — його ловить виконавець черги (<c>JobWorker</c>) і нічого
/// не пише в рядок задачі (рядок уже належить іншому).
/// </para>
/// </remarks>
public sealed class JobLeaseLostException : Exception
{
    /// <summary>Створює виняток для задачі.</summary>
    /// <param name="jobId">Задача, оренду якої втрачено.</param>
    public JobLeaseLostException(string jobId)
        : base($"Оренду задачі {jobId} втрачено: її виконує інший виконавець.")
        => JobId = jobId;

    /// <summary>Задача, оренду якої втрачено.</summary>
    public string JobId { get; }
}
