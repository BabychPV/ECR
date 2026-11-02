-- PERMITS: permits ── ECR-PRM-4095 ── ru/kz: дозвіл уже існує, випадки 0404/0422 екземплярів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0404.permit', N'ru', N'Разрешения {id} не существует.'),
    (N'err.ECR-PRM-0404.permit', N'kz', N'{id} рұқсаты жоқ.'),
    (N'err.ECR-PRM-0422.numberRequired', N'ru', N'Номер разрешения обязателен.'),
    (N'err.ECR-PRM-0422.numberRequired', N'kz', N'Рұқсат нөмірі міндетті.'),
    (N'err.ECR-PRM-0422.numberTooLong', N'ru', N'Номер разрешения не должен быть длиннее {max} символов.'),
    (N'err.ECR-PRM-0422.numberTooLong', N'kz', N'Рұқсат нөмірі {max} таңбадан ұзын болмауы керек.'),
    (N'err.ECR-PRM-0422.locationTooLong', N'ru', N'Расположение не должно быть длиннее {max} символов.'),
    (N'err.ECR-PRM-0422.locationTooLong', N'kz', N'Орналасқан жері {max} таңбадан ұзын болмауы керек.'),
    (N'err.ECR-PRM-0422.validityOrder', N'ru', N'Разрешение должно заканчиваться после начала: {from} - {to}. Дата окончания - первый день, когда оно уже не действует.'),
    (N'err.ECR-PRM-0422.validityOrder', N'kz', N'Рұқсат басталғаннан кейін аяқталуы керек: {from} - {to}. Аяқталу күні - ол енді жарамсыз болатын алғашқы күн.'),
    (N'err.ECR-PRM-0422.typeNotPublished', N'ru', N'Тип разрешения «{typeCode}» ещё не опубликован: разрешение по нему создать нельзя.'),
    (N'err.ECR-PRM-0422.typeNotPublished', N'kz', N'«{typeCode}» рұқсат түрі әлі жарияланбаған: ол бойынша рұқсат жасау мүмкін емес.'),
    (N'err.ECR-PRM-4095', N'ru', N'Разрешение уже существует'),
    (N'err.ECR-PRM-4095', N'kz', N'Рұқсат бұрыннан бар'),
    (N'err.ECR-PRM-4095.number', N'ru', N'Разрешение «{number}» типа «{typeCode}» с началом {validFrom} уже существует (Id {id}).'),
    (N'err.ECR-PRM-4095.number', N'kz', N'«{typeCode}» түріндегі {validFrom} басталатын «{number}» рұқсаты бұрыннан бар (Id {id}).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: permits ── ECR-PRM-4095 ── ru/kz ── кінець секції ──

