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

-- PERMITS: permits ── en: ECR-PRM-4095 і випадки 0404/0422 екземплярів дозволів (PM-04) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-0404.permit', N'en', N'Permit {id} does not exist.', 1),
    (N'err.ECR-PRM-0422.numberRequired', N'en', N'The permit number is required.', 1),
    (N'err.ECR-PRM-0422.numberTooLong', N'en', N'The permit number must not be longer than {max} characters.', 1),
    (N'err.ECR-PRM-0422.locationTooLong', N'en', N'The location must not be longer than {max} characters.', 1),
    (N'err.ECR-PRM-0422.validityOrder', N'en', N'The permit must end after it starts: {from} - {to}. The end date is the first day it is no longer valid.', 1),
    (N'err.ECR-PRM-0422.typeNotPublished', N'en', N'The permit type "{typeCode}" is not published yet, so a permit cannot be created for it.', 1),
    (N'err.ECR-PRM-4095', N'en', N'Permit already exists', 1),
    (N'err.ECR-PRM-4095.number', N'en', N'A permit "{number}" of type "{typeCode}" valid from {validFrom} already exists (id {id}).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: permits ── ECR-PRM-4095 ── кінець секції ──
