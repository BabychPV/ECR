using Ecr.Application.Ports;

namespace Ecr.Application.Documents.Dto;

/// <summary>✎ D-450: власники видимих читачеві документів — варіанти фільтра переліку «Власник».</summary>
/// <param name="Items">
/// Люди за спаданням кількості документів, далі за іменем; не більше 50 (<c>ListDocumentsHandler.MaxOwners</c>) —
/// решту знаходить пошук <c>q</c> за іменем.
/// </param>
public sealed record DocumentOwnersResponse(IReadOnlyList<DocumentOwnerOption> Items);
