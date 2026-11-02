-- COLL:an42vm ── ru/kz T2-07/T3-03/T4-06: повідомлення валідації за ключем; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'validation.check.mismatch', N'ru', N'Сверка: {left} = {leftValue} не сходится с {right} = {rightValue}: отклонение {deviation}, допустимо {allowed} ({kind}).'),
    (N'validation.column.required', N'ru', N'Колонка «{column}» обязательна.'),
    (N'validation.column.scale', N'ru', N'Колонка «{column}» допускает не более {scale} знаков после запятой.'),
    (N'validation.column.precision', N'ru', N'Значение не помещается в точность колонки «{column}» ({precision} цифр).'),
    (N'validation.rule.parseError', N'ru', N'Правило ''{rule}'' не разбирается: {detail}'),
    (N'validation.rule.notLogical', N'ru', N'Правило ''{rule}'' не дало логического ответа: {reason}'),
    (N'validation.check.mismatch', N'kz', N'Салыстыру: {left} = {leftValue} мәні {right} = {rightValue} мәніне сәйкес келмейді: ауытқу {deviation}, рұқсат етілгені {allowed} ({kind}).'),
    (N'validation.column.required', N'kz', N'«{column}» бағаны міндетті.'),
    (N'validation.column.scale', N'kz', N'«{column}» бағанында үтірден кейін {scale} таңбадан артық болмауы керек.'),
    (N'validation.column.precision', N'kz', N'Мән «{column}» бағанының дәлдігіне ({precision} сан) сыймайды.'),
    (N'validation.rule.parseError', N'kz', N'''{rule}'' ережесі талдана алмайды: {detail}'),
    (N'validation.rule.notLogical', N'kz', N'''{rule}'' ережесі логикалық жауап бермеді: {reason}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an42vm ── кінець секції ──
