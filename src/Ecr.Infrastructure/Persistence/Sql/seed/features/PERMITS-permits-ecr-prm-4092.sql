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
-- PERMITS: permits ── en: читання дозволів — фільтри переліку (PM-04) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-0422.statusUnknown', N'en', N'The status "{status}" does not exist. Use Draft, Active or Expired.', 1),
    (N'err.ECR-PRM-0422.cursorInvalid', N'en', N'The page cursor of the permit list is not valid. Start from the first page.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: permits ── читання ── кінець секції ──

-- PERMITS: permits ── en: ECR-PRM-4092 і життєвий цикл дозволу (PM-04) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-4092', N'en', N'Permit validity overlaps', 1),
    (N'err.ECR-PRM-4092.overlap', N'en', N'The permit "{number}" ({id}) overlaps in time with the active permit {otherId} of the same number.', 1),
    (N'err.ECR-PRM-4096.notActive', N'en', N'The permit {id} is {status}: this is only possible for an active permit.', 1),
    (N'err.ECR-PRM-4096.notRenewable', N'en', N'The permit {id} is a draft: only an active or expired permit can be renewed.', 1),
    (N'err.ECR-PRM-0422.endReasonTooShort', N'en', N'The reason for ending the permit early must have at least {min} characters.', 1),
    (N'err.ECR-PRM-0422.endDateOutOfRange', N'en', N'The end date {endDate} must be after the start {from} and not later than the current end {to}.', 1),
    (N'err.ECR-PRM-0422.renewRowInvalid', N'en', N'Row "{rowCode}" of table "{tableCode}" could not be copied into the new draft: {reason}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: permits ── ECR-PRM-4092 ── кінець секції ──
