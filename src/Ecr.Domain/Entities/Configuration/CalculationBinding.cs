using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Прив'язка результату методології до колонки документа.
/// Значення **не копіюється** в <c>doc.CellValue</c> — воно читається за
/// посиланням (D-69): інакше нічний перерахунок писав би десятки мільйонів
/// рядків у партиції документів і роздував аудит.
/// </summary>
public sealed class CalculationBinding : Entity<int>
{
    private CalculationBinding() { }

    public CalculationBinding(int tableDefId, int columnDefId, int methodologyId, string outputCode, string matchJson)
    {
        TableDefId = tableDefId;
        ColumnDefId = columnDefId;
        MethodologyId = methodologyId;
        OutputCode = outputCode;
        MatchJson = matchJson;
        IsActive = true;
    }

    public int TableDefId { get; private set; }
    public int ColumnDefId { get; private set; }
    public int MethodologyId { get; private set; }

    /// <summary>Змінює предикат зіставлення й активність наявної прив'язки.</summary>
    /// <param name="matchJson">Структурований предикат зіставлення рядків.</param>
    /// <param name="isActive">Чи бере прив'язка участь у прогоні.</param>
    /// <exception cref="DomainException">Порожній предикат.</exception>
    /// <remarks>
    /// ⛔ Адреса прив'язки — трійка <c>(ColumnDefId, MethodologyId, OutputCode)</c>
    /// (<c>UQ_CalculationBinding</c>), і саме її змінити не можна: інша трійка —
    /// це інша прив'язка, а не редакція цієї. Мутатор дає лише те, що ЗМІНЮЄТЬСЯ
    /// в межах однієї адреси; без нього повторний запис за тією самою адресою
    /// мусив би видаляти рядок і створювати новий, тобто міняти <c>Id</c>, на
    /// який уже посилаються.
    ///
    /// ⚠ <c>TableDefId</c> теж не змінюється: він виводиться з
    /// <see cref="ColumnDefId"/>, а колонка не переїжджає між таблицями.
    /// Прив'язка, у якій ці двоє розійшлися, тихо не спрацювала б —
    /// <c>RecalculationJob.BindingsAsync</c> зіставляє екземпляри таблиць саме
    /// за <c>TableDefId</c>.
    /// </remarks>
    public void Update(string matchJson, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(matchJson))
        {
            throw new DomainException(
                "ECR-CFG-0422",
                $"Прив'язка виходу «{OutputCode}» без предиката зіставлення: "
                + "порожній рядок не є ні «вся таблиця» (це `{}`), ні звуженням.");
        }

        MatchJson = matchJson;
        IsActive = isActive;
    }

    /// <summary>Який вихід методології (<c>tons</c>, <c>gsec</c>).</summary>
    public string OutputCode { get; private set; } = null!;

    /// <summary>Як зіставити рядок документа з результатом розрахунку.</summary>
    public string MatchJson { get; private set; } = null!;

    public bool IsActive { get; private set; }
}
