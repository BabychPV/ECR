-- PERMITS: bindings ── ECR-PRM-4101 ── ru/kz: прив'язка вже є, невідомі тип/таблиця/значення; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0422.bindingTypeUnknown', N'ru', N'Типа разрешения «{typeCode}» не существует.'),
    (N'err.ECR-PRM-0422.bindingTypeUnknown', N'kz', N'«{typeCode}» рұқсат түрі жоқ.'),
    (N'err.ECR-PRM-0422.bindingPermitTableUnknown', N'ru', N'У типа разрешения «{typeCode}» нет таблицы «{tableCode}».'),
    (N'err.ECR-PRM-0422.bindingPermitTableUnknown', N'kz', N'«{typeCode}» рұқсат түрінде «{tableCode}» кестесі жоқ.'),
    (N'err.ECR-PRM-0422.bindingEnumUnknown', N'ru', N'Значение «{value}» поля «{field}» неизвестно.'),
    (N'err.ECR-PRM-0422.bindingEnumUnknown', N'kz', N'«{field}» өрісінің «{value}» мәні белгісіз.'),
    (N'err.ECR-PRM-0422.bindingIdentityChanged', N'ru', N'Привязку «{bindingCode}» нельзя перенацелить на другую таблицу или тип: удалите её и создайте новую.'),
    (N'err.ECR-PRM-0422.bindingIdentityChanged', N'kz', N'«{bindingCode}» байланысын басқа кестеге не түрге бағыттауға болмайды: оны жойып, жаңасын жасаңыз.'),
    (N'err.ECR-PRM-4101', N'ru', N'Привязка разрешения уже есть'),
    (N'err.ECR-PRM-4101', N'kz', N'Рұқсат байланысы бұрыннан бар'),
    (N'err.ECR-PRM-4101.sheet', N'ru', N'Таблица {tableDefId} этой версии уже привязана («{bindingCode}»): одна таблица листа — одно разрешение.'),
    (N'err.ECR-PRM-4101.sheet', N'kz', N'Осы нұсқаның {tableDefId} кестесі байланған («{bindingCode}»): бір парақ кестесі — бір рұқсат.'),
    (N'err.ECR-PRM-4101.code', N'ru', N'Код привязки «{bindingCode}» уже занят (коды уникальны во всей системе).'),
    (N'err.ECR-PRM-4101.code', N'kz', N'«{bindingCode}» байланыс коды бос емес (кодтар бүкіл жүйеде бірегей).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: bindings ── ECR-PRM-4101 ── кінець секції ──

