-- PERMITS: groups-api ── ru/kz: відмови груп типів дозволів (PRMG); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0405', N'ru', N'Группа разрешений не найдена'),
    (N'err.ECR-PRM-0405', N'kz', N'Рұқсаттар тобы табылмады'),
    (N'err.ECR-PRM-0405.group', N'ru', N'Группы разрешений «{code}» не существует.'),
    (N'err.ECR-PRM-0405.group', N'kz', N'«{code}» рұқсаттар тобы жоқ.'),
    (N'err.ECR-PRM-4097', N'ru', N'Группа разрешений уже существует'),
    (N'err.ECR-PRM-4097', N'kz', N'Рұқсаттар тобы бұрыннан бар'),
    (N'err.ECR-PRM-4097.code', N'ru', N'Группа разрешений «{code}» уже существует.'),
    (N'err.ECR-PRM-4097.code', N'kz', N'«{code}» рұқсаттар тобы бұрыннан бар.'),
    (N'err.ECR-PRM-4097.registryCode', N'ru', N'Код реестра «{registryCode}», нужный группе, уже занят.'),
    (N'err.ECR-PRM-4097.registryCode', N'kz', N'Топқа қажетті «{registryCode}» тізілім коды бос емес.'),
    (N'err.ECR-PRM-4098', N'ru', N'Группа разрешений используется'),
    (N'err.ECR-PRM-4098', N'kz', N'Рұқсаттар тобы қолданыста'),
    (N'err.ECR-PRM-4098.inUse', N'ru', N'Группу разрешений «{code}» используют шаблоны ({templates}), удалить её нельзя. Деактивируйте её.'),
    (N'err.ECR-PRM-4098.inUse', N'kz', N'«{code}» рұқсаттар тобын үлгілер пайдаланады ({templates}), оны жоюға болмайды. Оны өшіріп қойыңыз.'),
    (N'err.ECR-PRM-4098.referencedByHistory', N'ru', N'Группу разрешений «{code}» удалить нельзя: на неё остались исторические ссылки (мягко удалённые колонки или поля шаблонов). Деактивируйте её.'),
    (N'err.ECR-PRM-4098.referencedByHistory', N'kz', N'«{code}» рұқсаттар тобын жоюға болмайды: оған тарихи сілтемелер қалды (үлгілердің жұмсақ жойылған бағандары немесе өрістері). Оны өшіріп қойыңыз.'),
    (N'err.ECR-PRM-4099', N'ru', N'Группа разрешений изменена'),
    (N'err.ECR-PRM-4099', N'kz', N'Рұқсаттар тобы өзгертілді'),
    (N'err.ECR-PRM-4099.changed', N'ru', N'Группу разрешений «{code}» изменили после того, как вы её открыли. Перечитайте её и повторите правку.'),
    (N'err.ECR-PRM-4099.changed', N'kz', N'«{code}» рұқсаттар тобын сіз ашқаннан кейін өзгертті. Оны қайта оқып, түзетуді қайталаңыз.'),
    (N'err.ECR-PRM-0422.permitGroupNameRequired', N'ru', N'Название группы разрешений обязательно хотя бы на одном языке.'),
    (N'err.ECR-PRM-0422.permitGroupNameRequired', N'kz', N'Рұқсаттар тобының атауы кемінде бір тілде міндетті.'),
    (N'err.ECR-PRM-0422.permitGroupCodeInvalid', N'ru', N'Код группы «{code}» недопустим: буквы, цифры и подчёркивание, начиная с буквы, до 59 символов.'),
    (N'err.ECR-PRM-0422.permitGroupCodeInvalid', N'kz', N'«{code}» топ коды жарамсыз: әріптер, цифрлар және асты сызылған таңба, әріптен басталады, 59 таңбаға дейін.'),
    (N'err.ECR-PRM-0422.permitGroupTextTooLong', N'ru', N'Текст группы ({item}) длиннее {max} символов.'),
    (N'err.ECR-PRM-0422.permitGroupTextTooLong', N'kz', N'Топ мәтіні ({item}) {max} таңбадан ұзын.'),
    (N'err.ECR-PRM-0422.permitGroupMemberDuplicate', N'ru', N'Тип разрешения «{typeCode}» указан в группе больше одного раза.'),
    (N'err.ECR-PRM-0422.permitGroupMemberDuplicate', N'kz', N'«{typeCode}» рұқсат түрі топта бірнеше рет көрсетілген.'),
    (N'err.ECR-PRM-0422.permitGroupMemberUnknown', N'ru', N'Типа разрешения «{typeCode}» не существует.'),
    (N'err.ECR-PRM-0422.permitGroupMemberUnknown', N'kz', N'«{typeCode}» рұқсат түрі жоқ.'),
    (N'err.ECR-PRM-0422.permitGroupMemberNotPublished', N'ru', N'Тип разрешения «{typeCode}» ещё не опубликован: в группу входят только опубликованные типы.'),
    (N'err.ECR-PRM-0422.permitGroupMemberNotPublished', N'kz', N'«{typeCode}» рұқсат түрі әлі жарияланбаған: топқа тек жарияланған түрлер кіреді.'),
    (N'err.ECR-PRM-0422.permitGroupRegistryManaged', N'ru', N'Реестр «{registryCode}» принадлежит группе разрешений и меняется только через группу.'),
    (N'err.ECR-PRM-0422.permitGroupRegistryManaged', N'kz', N'«{registryCode}» тізілімі рұқсаттар тобына тиесілі және тек топ арқылы өзгереді.'),
    (N'err.ECR-PRM-4097.registryReserved', N'ru', N'Префикс кода «{prefix}» зарезервирован за группами разрешений: код реестра «{registryCode}» использовать нельзя.'),
    (N'err.ECR-PRM-4097.registryReserved', N'kz', N'«{prefix}» код префиксі рұқсаттар топтарына резервтелген: «{registryCode}» тізілім кодын пайдалануға болмайды.'),
    (N'err.ECR-CALC-0422.rowActivityFilterPermitGroup', N'ru', N'Фильтр активности строк не может указывать на группу разрешений «{registryCode}»: в группе нет записей.'),
    (N'err.ECR-CALC-0422.rowActivityFilterPermitGroup', N'kz', N'Жолдардың белсенділік сүзгісі «{registryCode}» рұқсаттар тобына сілтей алмайды: топта жазбалар жоқ.'),
    (N'err.ECR-TMPL-0422.lookupGroupNoRegistryForms', N'ru', N'Колонка {columnCode} использует группу разрешений как список: функции REG* над ней пока не поддерживаются.'),
    (N'err.ECR-TMPL-0422.lookupGroupNoRegistryForms', N'kz', N'{columnCode} бағаны тізім ретінде рұқсаттар тобын пайдаланады: оның үстінде REG* функциялары әзірге қолдау таппайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: groups-api ── кінець секції ──
