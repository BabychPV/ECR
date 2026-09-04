// src/Ecr.Application/Ports/IExternalDataSource.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;

/// <summary>
/// Читання із зовнішнього джерела. PI AF — <b>виключно джерело</b>: система в
/// нього нічого не пише (D-44), тому парного <c>IExternalDataSink</c> не існує.
/// </summary>
public interface IExternalDataSource
{
    /// <summary>Транспорт, який реалізує адаптер.</summary>
    ExternalTransport Transport { get; }

    /// <summary>Каталог сутностей джерела — для конфігуратора, щоб не вводити імена руками.</summary>
    Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct);

    /// <summary>
    /// Читає діапазон. Ідемпотентно: повторний запуск того самого діапазону не
    /// дублює даних (ФВ-11.3).
    /// </summary>
    Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct);
}
