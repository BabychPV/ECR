-- COLL:auditreason ── ru/kz причин зміни налаштувань збору (IntegrationConfigAudit.Reason); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'integrationAudit.scheduleCreated', N'ru', N'Расписание сбора {id} создано для сущности «{entity}».'),
    (N'integrationAudit.scheduleCreated', N'kz', N'«{entity}» нысаны үшін {id} жинау жоспары құрылды.'),
    (N'integrationAudit.scheduleChanged', N'ru', N'Расписание сбора {id} сущности «{entity}» изменено.'),
    (N'integrationAudit.scheduleChanged', N'kz', N'«{entity}» нысанының {id} жинау жоспары өзгертілді.'),
    (N'integrationAudit.scheduleDeleted', N'ru', N'Расписание сбора {id} сущности «{entity}» удалено.'),
    (N'integrationAudit.scheduleDeleted', N'kz', N'«{entity}» нысанының {id} жинау жоспары жойылды.'),
    (N'integrationAudit.fieldMapCreated', N'ru', N'Сопоставление поля «{field}» сущности {entity} создано.'),
    (N'integrationAudit.fieldMapCreated', N'kz', N'{entity} нысанының «{field}» өрісін сәйкестендіру құрылды.'),
    (N'integrationAudit.fieldMapPaused', N'ru', N'Сопоставление поля «{field}» приостановлено.'),
    (N'integrationAudit.fieldMapPaused', N'kz', N'«{field}» өрісін сәйкестендіру тоқтатылды.'),
    (N'integrationAudit.fieldMapResumed', N'ru', N'Сопоставление поля «{field}» возвращено в сбор.'),
    (N'integrationAudit.fieldMapResumed', N'kz', N'«{field}» өрісін сәйкестендіру жинауға қайтарылды.'),
    (N'integrationAudit.fieldMapUnitAccepted', N'ru', N'Сопоставление поля «{field}»: принята новая единица источника.'),
    (N'integrationAudit.fieldMapUnitAccepted', N'kz', N'«{field}» өрісін сәйкестендіру: көздің жаңа өлшем бірлігі қабылданды.'),
    (N'integrationAudit.fieldMapDeleted', N'ru', N'Сопоставление поля «{field}» сущности {entity} удалено.'),
    (N'integrationAudit.fieldMapDeleted', N'kz', N'{entity} нысанының «{field}» өрісін сәйкестендіру жойылды.'),
    (N'integrationAudit.rowWindowCreated', N'ru', N'Привязка окна строки к столбцу «{column}» создана.'),
    (N'integrationAudit.rowWindowCreated', N'kz', N'Жол терезесін «{column}» бағанына байлау құрылды.'),
    (N'integrationAudit.rowWindowChanged', N'ru', N'Привязка окна строки {id} изменена.'),
    (N'integrationAudit.rowWindowChanged', N'kz', N'{id} жол терезесін байлау өзгертілді.'),
    (N'integrationAudit.rowWindowDeleted', N'ru', N'Привязка окна строки {id} удалена.'),
    (N'integrationAudit.rowWindowDeleted', N'kz', N'{id} жол терезесін байлау жойылды.'),
    (N'integrationAudit.sourceEntityCreated', N'ru', N'Сущность сбора «{entity}» добавлена в подключение «{connection}».'),
    (N'integrationAudit.sourceEntityCreated', N'kz', N'«{entity}» жинау нысаны «{connection}» қосылымына қосылды.'),
    (N'integrationAudit.sourceEntityBound', N'ru', N'Сущность сбора «{entity}» привязана к справочнику {registry}.'),
    (N'integrationAudit.sourceEntityBound', N'kz', N'«{entity}» жинау нысаны {registry} анықтамалығына байланыстырылды.'),
    (N'integrationAudit.sourceEntityUnbound', N'ru', N'Сущность сбора «{entity}» отвязана от справочника.'),
    (N'integrationAudit.sourceEntityUnbound', N'kz', N'«{entity}» жинау нысаны анықтамалықтан ажыратылды.'),
    (N'integrationAudit.eventMapCreated', N'ru', N'Сопоставление событий сущности {entity} с документом {document} создано.'),
    (N'integrationAudit.eventMapCreated', N'kz', N'{entity} нысанының оқиғаларын {document} құжатымен сәйкестендіру құрылды.'),
    (N'integrationAudit.eventMapChanged', N'ru', N'Сопоставление событий {id} сущности {entity} изменено.'),
    (N'integrationAudit.eventMapChanged', N'kz', N'{entity} нысанының {id} оқиғаларды сәйкестендіруі өзгертілді.'),
    (N'integrationAudit.eventMapDeleted', N'ru', N'Сопоставление событий {id} сущности {entity} удалено.'),
    (N'integrationAudit.eventMapDeleted', N'kz', N'{entity} нысанының {id} оқиғаларды сәйкестендіруі жойылды.'),
    (N'integrationAudit.registryPolicyChanged', N'ru', N'Политика синхронизации справочника {registry} из сущности «{entity}» изменена.'),
    (N'integrationAudit.registryPolicyChanged', N'kz', N'«{entity}» нысанынан {registry} анықтамалығын синхрондау саясаты өзгертілді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:auditreason ── кінець секції ──

