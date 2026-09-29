// src/Ecr.Domain/Enums/RegistryMissingPolicy.cs
namespace Ecr.Domain.Enums;

/// <summary>
/// Що робить синк довідника з записом, чий елемент зник із джерела
/// (<c>D-212</c>). Колонка <c>ext.SourceEntity.OnMissingInSource</c>.
/// </summary>
/// <remarks>
/// ⛔ Жодне значення не видаляє запис фізично: у комірках лежить його
/// <c>Id</c> (ФВ-8.6), і зникнення елемента в AF не робить історію неправдою.
/// </remarks>
public enum RegistryMissingPolicy : byte
{
    /// <summary>
    /// Лише позначити зв'язок (<c>MissingInSourceSince</c>); запис лишається
    /// активним. Умовчання: найменш руйнівне.
    /// </summary>
    MarkOrphaned = 0,

    /// <summary>
    /// Позначити зв'язок і вимкнути запис (<c>IsActive = 0</c>). Повернення
    /// елемента (<c>D-212</c> Q6): <c>External</c> — запис вмикається сам із
    /// подією <c>RegistryReactivated</c>; <c>Hybrid</c> — вмикає людина.
    /// </summary>
    Deactivate = 1,

    /// <summary>
    /// Запис не чіпається; синк пише подію <c>RegistrySourceMissing</c>
    /// (<c>D-212</c> Q5).
    /// </summary>
    Ignore = 2,
}
