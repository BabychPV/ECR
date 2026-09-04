// src/Ecr.Application/Localization/SetUiStringHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Localization;

/// <summary>Зміна рядка каталогу; право <c>System.ManageLocalization</c>.</summary>
public sealed class SetUiStringHandler(
    IUnitOfWork uow, IAuditWriter audit, ICurrentUser currentUser, IClock clock)
{
    public Task<int> HandleAsync(string key, string languageCode, string value, byte scope, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) upsert рядка;\n" +
            "2) інкремент UiStringRevision ОДНИМ statement із OUTPUT — той самий " +
            "   прийом, що з PresentationRevision (R-B7): два адміністратори, що " +
            "   правлять переклад одночасно, не мають отримати однакову версію;\n" +
            "3) зміна scope з Private на Public — рішення про видимість, тому " +
            "   в аудит із автором;\n" +
            "4) повернути нову Revision.");
}
