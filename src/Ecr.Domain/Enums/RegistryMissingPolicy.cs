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

    /// <summary>Позначити зв'язок і вимкнути запис (<c>IsActive = 0</c>).</summary>
    Deactivate = 1,

    /// <summary>Нічого не робити: зникнення не фіксується.</summary>
    Ignore = 2,
}
