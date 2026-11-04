-- PERMITS: lists-api ── ru/kz: відмови списків вибору дозволів (PM-08e); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0404.list', N'ru', N'Списка «{key}» не существует.'),
    (N'err.ECR-PRM-0404.list', N'kz', N'«{key}» тізімі жоқ.'),
    (N'err.ECR-PRM-4094.valueInUse', N'ru', N'Значение «{value}» уже выбрано в разрешениях, поэтому его английскую подпись менять нельзя. Добавьте новое значение; подписи на других языках править можно.'),
    (N'err.ECR-PRM-4094.valueInUse', N'kz', N'«{value}» мәні рұқсаттарда таңдалып қойған, сондықтан оның ағылшынша атауын өзгертуге болмайды. Жаңа мән қосыңыз; басқа тілдердегі атауларды өңдеуге болады.'),
    (N'err.ECR-PRM-0422.listReadOnly', N'ru', N'Список «{key}» — таблица того же разрешения: его значения — строки разрешения, поэтому они правятся в самом разрешении.'),
    (N'err.ECR-PRM-0422.listReadOnly', N'kz', N'«{key}» тізімі — сол рұқсаттың кестесі: оның мәндері — рұқсат жолдары, сондықтан олар рұқсаттың өзінде өңделеді.'),
    (N'err.ECR-PRM-0422.listChangeInvalid', N'ru', N'Изменение списка неполное или действие «{action}» неизвестно. Передайте от 1 до 200 изменений: Add, Update или Remove.'),
    (N'err.ECR-PRM-0422.listChangeInvalid', N'kz', N'Тізімді өзгерту толық емес немесе «{action}» әрекеті белгісіз. 1-ден 200-ге дейін өзгеріс жіберіңіз: Add, Update немесе Remove.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: lists-api ── кінець секції ──
-- PERMITS: lists-api ── en: відмови списків вибору дозволів (ECR-PRM-0404 list, ECR-PRM-0422 listReadOnly/listChangeInvalid; PM-08e); ru/kz — порцією PERMITS: lists-api нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-0404.list', N'en', N'The list "{key}" does not exist.', 1),
    (N'err.ECR-PRM-0422.listReadOnly', N'en', N'The list "{key}" is a table of the same permit: its values are the permit rows, so they are edited in the permit.', 1),
    (N'err.ECR-PRM-0422.listChangeInvalid', N'en', N'The list change is incomplete or its action "{action}" is unknown. Send 1 to 200 changes: Add, Update or Remove.', 1),
    (N'err.ECR-PRM-4094.valueInUse', N'en', N'The value "{value}" is already chosen in permits, so its English label cannot be changed. Add a new value instead; labels in other languages can be edited.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: lists-api ── кінець секції ──
