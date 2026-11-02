-- PERMITS: permits ── ECR-PRM-4092 ── ru/kz: перетин чинності, життєвий цикл дозволу; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-4092', N'ru', N'Сроки действия разрешений пересекаются'),
    (N'err.ECR-PRM-4092', N'kz', N'Рұқсаттардың жарамдылық мерзімдері қиылысады'),
    (N'err.ECR-PRM-4092.overlap', N'ru', N'Разрешение «{number}» ({id}) пересекается по времени с действующим разрешением {otherId} того же номера.'),
    (N'err.ECR-PRM-4092.overlap', N'kz', N'«{number}» ({id}) рұқсаты сол нөмірдегі {otherId} жарамды рұқсатымен уақыт бойынша қиылысады.'),
    (N'err.ECR-PRM-4096.notActive', N'ru', N'Разрешение {id} в состоянии {status}: это возможно только для действующего разрешения.'),
    (N'err.ECR-PRM-4096.notActive', N'kz', N'{id} рұқсаты {status} күйінде: бұл тек жарамды рұқсат үшін мүмкін.'),
    (N'err.ECR-PRM-4096.notRenewable', N'ru', N'Разрешение {id} - черновик: продлевать можно только действующее или закончившееся разрешение.'),
    (N'err.ECR-PRM-4096.notRenewable', N'kz', N'{id} рұқсаты - жоба: тек жарамды немесе аяқталған рұқсатты ұзартуға болады.'),
    (N'err.ECR-PRM-0422.endReasonTooShort', N'ru', N'Причина досрочного завершения разрешения должна содержать не менее {min} символов.'),
    (N'err.ECR-PRM-0422.endReasonTooShort', N'kz', N'Рұқсатты мерзімінен бұрын аяқтау себебі кемінде {min} таңба болуы керек.'),
    (N'err.ECR-PRM-0422.endDateOutOfRange', N'ru', N'Дата завершения {endDate} должна быть позже начала {from} и не позже текущего окончания {to}.'),
    (N'err.ECR-PRM-0422.endDateOutOfRange', N'kz', N'Аяқталу күні {endDate} басталудан {from} кейін және ағымдағы аяқталудан {to} кеш болмауы керек.'),
    (N'err.ECR-PRM-0422.renewRowInvalid', N'ru', N'Строку «{rowCode}» таблицы «{tableCode}» не удалось скопировать в новый черновик: {reason}.'),
    (N'err.ECR-PRM-0422.renewRowInvalid', N'kz', N'«{tableCode}» кестесінің «{rowCode}» жолын жаңа жобаға көшіру мүмкін болмады: {reason}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: permits ── ECR-PRM-4092 ── ru/kz ── кінець секції ──
