-- COLL:d212-ui ── ru/kz форми політики синку довідника (D-212 PR-8); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'sources.syncPolicy', N'ru', N'Политика синхронизации'),
    (N'sources.syncPolicy', N'kz', N'Синхрондау саясаты'),
    (N'sources.syncPolicyTitle', N'ru', N'Политика синхронизации справочника: {entity}'),
    (N'sources.syncPolicyTitle', N'kz', N'Анықтамалықты синхрондау саясаты: {entity}'),
    (N'sources.syncPolicyHint', N'ru', N'Как синхронизация справочника обходится с элементами этой сущности, исчезнувшими из источника, и какие атрибуты элемента несут даты действия записи.'),
    (N'sources.syncPolicyHint', N'kz', N'Анықтамалықты синхрондау осы нысанның деректер көзінен жоғалған элементтерімен не істейді және элементтің қай атрибуттары жазбаның қолданылу күндерін береді.'),
    (N'sources.syncPolicyMissing', N'ru', N'Когда элемент исчезает из источника'),
    (N'sources.syncPolicyMissing', N'kz', N'Элемент деректер көзінен жоғалғанда'),
    (N'sources.syncPolicyMarkOrphaned', N'ru', N'Отметить как отсутствующий'),
    (N'sources.syncPolicyMarkOrphaned', N'kz', N'Жоқ деп белгілеу'),
    (N'sources.syncPolicyMarkOrphanedHint', N'ru', N'Запись остаётся включённой; её связь помечается отсутствующей в источнике с даты прогона, пишется событие.'),
    (N'sources.syncPolicyMarkOrphanedHint', N'kz', N'Жазба қосулы қалады; оның байланысы іске қосу күнінен бастап деректер көзінде жоқ деп белгіленеді, оқиға жазылады.'),
    (N'sources.syncPolicyIgnore', N'ru', N'Только сообщить'),
    (N'sources.syncPolicyIgnore', N'kz', N'Тек хабарлау'),
    (N'sources.syncPolicyIgnoreHint', N'ru', N'Ни запись, ни её связь не меняются; пишется только событие.'),
    (N'sources.syncPolicyIgnoreHint', N'kz', N'Жазба да, оның байланысы да өзгермейді; тек оқиға жазылады.'),
    (N'sources.syncPolicyDeactivate', N'ru', N'Отключить запись'),
    (N'sources.syncPolicyDeactivate', N'kz', N'Жазбаны өшіру'),
    (N'sources.syncPolicyDeactivateHint', N'ru', N'Запись отключается, её связь помечается отсутствующей. Если элемент вернётся, справочник, ведущийся источником, включит запись сам; гибридный ждёт человека.'),
    (N'sources.syncPolicyDeactivateHint', N'kz', N'Жазба өшіріледі, оның байланысы жоқ деп белгіленеді. Элемент қайтса, деректер көзі жүргізетін анықтамалық жазбаны өзі қосады; гибридті анықтамалық адамды күтеді.'),
    (N'sources.syncPolicyAttributeHint', N'ru', N'Даты действия: имена атрибутов элемента в источнике. Оставьте пустым, чтобы не синхронизировать дату.'),
    (N'sources.syncPolicyAttributeHint', N'kz', N'Қолданылу күндері: деректер көзіндегі элемент атрибуттарының атаулары. Күнді синхрондамау үшін бос қалдырыңыз.'),
    (N'sources.syncPolicyValidFrom', N'ru', N'Атрибут начала действия'),
    (N'sources.syncPolicyValidFrom', N'kz', N'Қолданылу басының атрибуты'),
    (N'sources.syncPolicyValidTo', N'ru', N'Атрибут окончания действия'),
    (N'sources.syncPolicyValidTo', N'kz', N'Қолданылу соңының атрибуты'),
    (N'sources.syncPolicyValidToInclusive', N'ru', N'Дата окончания в источнике — последний день действия (включительно)'),
    (N'sources.syncPolicyValidToInclusive', N'kz', N'Деректер көзіндегі аяқталу күні — қолданылудың соңғы күні (қоса алғанда)'),
    (N'sources.syncPolicySaved', N'ru', N'Политика синхронизации сохранена'),
    (N'sources.syncPolicySaved', N'kz', N'Синхрондау саясаты сақталды'),
    (N'registries.externalKeyMissing', N'ru', N'Нет в источнике'),
    (N'registries.externalKeyMissing', N'kz', N'Деректер көзінде жоқ'),
    (N'registries.externalKeyMissingSince', N'ru', N'С {date}'),
    (N'registries.externalKeyMissingSince', N'kz', N'{date} бастап')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d212-ui ── кінець секції ──

-- COLL:d212-ui ── Форма політики синку довідника з AF і позначка «зникло з джерела» (D-212 PR-8, `RegistrySyncPolicyModal`, `RegistryExternalKeysPanel`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'sources.syncPolicy',                     N'en', N'Sync policy', 1),
    (N'sources.syncPolicyTitle',                N'en', N'Registry sync policy: {entity}', 1),
    (N'sources.syncPolicyHint',                 N'en', N'How registry sync treats elements of this entity that disappear from the source, and which element attributes carry the validity dates of a record.', 1),
    (N'sources.syncPolicyMissing',              N'en', N'When an element disappears from the source', 1),
    (N'sources.syncPolicyMarkOrphaned',         N'en', N'Mark as missing', 1),
    (N'sources.syncPolicyMarkOrphanedHint',     N'en', N'The record stays active; its link is marked missing in source since the run date, and an event is written.', 1),
    (N'sources.syncPolicyIgnore',               N'en', N'Only report', 1),
    (N'sources.syncPolicyIgnoreHint',           N'en', N'Neither the record nor its link change; only an event is written.', 1),
    (N'sources.syncPolicyDeactivate',           N'en', N'Deactivate the record', 1),
    (N'sources.syncPolicyDeactivateHint',       N'en', N'The record is deactivated and its link is marked missing. If the element comes back, a registry mastered by the source is reactivated automatically; a hybrid one waits for a person.', 1),
    (N'sources.syncPolicyAttributeHint',        N'en', N'Validity dates: names of element attributes in the source. Leave empty to not synchronize the date.', 1),
    (N'sources.syncPolicyValidFrom',            N'en', N'Valid-from attribute', 1),
    (N'sources.syncPolicyValidTo',              N'en', N'Valid-to attribute', 1),
    (N'sources.syncPolicyValidToInclusive',     N'en', N'The end date in the source is the last valid day (inclusive)', 1),
    (N'sources.syncPolicySaved',                N'en', N'Sync policy saved', 1),
    (N'registries.externalKeyMissing',          N'en', N'Missing in source', 1),
    (N'registries.externalKeyMissingSince',     N'en', N'Since {date}', 1)
    -- ru/kz — окремою порцією `COLL:d212-ui` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d212-ui ── кінець секції ──
