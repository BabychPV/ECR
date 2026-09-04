using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Збір даних із зовнішнього джерела за розкладом.
/// </summary>
/// <remarks>
/// Простій джерела має бути **затримкою, а не втратою** (ФВ-11.3):
/// обслуговування PI AF відбуватиметься незалежно від нашої згоди. Тому
/// відмова джерела не робить задачу невдалою — діапазон іде в catch-up.
/// </remarks>
public sealed class CollectionJob(ICollectionStore collections) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: дістати sourceEntityId і діапазон із payload; викликати CollectionRunner " +
            "адаптера через порт IExternalDataSource, обраний за Transport ДЖЕРЕЛА (ФВ-11.2); " +
            "після успіху дописати покриття. ⚠ Задача має бути ідемпотентною: повторний " +
            "запуск того самого діапазону не дублює даних (природний ключ ext.RawDataPoint).");
}
