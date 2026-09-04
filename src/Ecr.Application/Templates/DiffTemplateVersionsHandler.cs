// src/Ecr.Application/Templates/DiffTemplateVersionsHandler.cs
using Ecr.Application.Ports;
using Ecr.Application.Templates.Dto;

namespace Ecr.Application.Templates;

/// <summary>
/// Diff двох версій за **ідентичністю**, не за позицією (ФВ-2.7, АРХ-2).
/// </summary>
/// <remarks>
/// Порівняння за `Ordinal` дало б «змінено все» після будь-якого
/// перевпорядкування — саме та хиба, через яку в чинному рішенні неможливо
/// зрозуміти, що насправді змінилося.
/// </remarks>
public sealed class DiffTemplateVersionsHandler(IMetadataCache metadata)
{
    public Task<TemplateDiffDto> HandleAsync(int fromVersionId, int toVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) зіставляти елементи за Code (колонки, аркуші, таблиці) і RowKey (рядки);\n" +
            "2) класифікувати кожну зміну: Compatible / Migratable / Breaking (ФВ-7.3);\n" +
            "3) окремо позначити презентаційні зміни — вони не потребують нової версії (ФВ-7.2);\n" +
            "4) повернути також ВПЛИВ: скільки документів прив'язано до fromVersionId.");
}
