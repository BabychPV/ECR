// src/Ecr.Application/Documents/Dto/TableStatusDto.cs

namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Заповненість однієї таблиці документа й кількість зауважень до неї
/// (<c>BE-10</c>) — те, з чого дерево аркушів складає «68 of 91 tables filled»
/// і крапку помилки біля таблиці.
/// </summary>
/// <remarks>
/// ⛔ <paramref name="InputCells"/> рахує ЛИШЕ те, що має заповнити ЛЮДИНА.
/// Формульні (<c>CellDataType.Formula</c>), обчислювані методологією
/// (<c>CellDataType.Calculated</c>), лише-для-читання колонки й рядки,
/// закриті правилом доступу до періоду, у знаменник НЕ входять. Без цього
/// «заповнено 40 %» означало б «60 % — формули», тобто число, яке виглядає як
/// прогрес, насправді вимірювало б частку формул у шаблоні.
///
/// ⛔ <paramref name="ErrorCount"/>/<paramref name="WarningCount"/> —
/// <b>nullable</b>, і <c>null</c> означає «перевірку за цей період ще не
/// запускали». Нуль тут був би тією самою неправдою, що й «0 зауважень» під
/// неперевіреним документом (<c>D15-06</c>, <c>A7-28</c>): зелений напис, на
/// який спираються, подаючи звітність. Той самий поділ уже тримає сусідній
/// маршрут <c>GET /documents/{id}/validation</c> — він віддає <c>404</c>
/// (<c>err.ECR-DOC-0404.notValidated</c>), а не порожній перелік. Тут замість
/// коду відповіді поділ несе сам тип поля: перелік таблиць віддається завжди,
/// бо заповненість відома й до першої перевірки.
/// </remarks>
/// <param name="TableDefId">Опис таблиці; ним клієнт зіставляє рядок із <c>DocumentTableDto</c>.</param>
/// <param name="SheetCode">Код аркуша, якому належить таблиця.</param>
/// <param name="FilledCells">Скільки вхідних комірок уже має значення.</param>
/// <param name="InputCells">Скільки комірок має заповнити людина.</param>
/// <param name="ErrorCount"><c>null</c> — не перевіряли; інакше — скільки помилок у цій таблиці.</param>
/// <param name="WarningCount"><c>null</c> — не перевіряли; інакше — скільки попереджень.</param>
public sealed record TableStatusDto(
    int TableDefId,
    string SheetCode,
    int FilledCells,
    int InputCells,
    int? ErrorCount,
    int? WarningCount);
