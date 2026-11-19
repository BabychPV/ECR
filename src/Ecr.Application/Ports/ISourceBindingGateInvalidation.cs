// src/Ecr.Application/Ports/ISourceBindingGateInvalidation.cs
namespace Ecr.Application.Ports;

/// <summary>
/// B179 L2 (perf): скидання процесного кешу воріт зв'язок джерел (<c>cfg.DocumentSourceBinding</c>), за яким хук
/// завершення перерахунку вирішує «документ — джерело?» без звернення до бази.
/// </summary>
/// <remarks>
/// ⚠ Скидає кеш лише ЦЬОГО процесу. Інший процес (Ecr.Worker, другий вузол Api) бачить зміну зв'язки не пізніше за
/// TTL кешу (<c>SourceBindingGateCache.Lifetime</c>).
/// </remarks>
public interface ISourceBindingGateInvalidation
{
    /// <summary>Зв'язку змінено (стан, біти) — наступне звернення перечитає ворота з бази.</summary>
    public void Invalidate();
}
