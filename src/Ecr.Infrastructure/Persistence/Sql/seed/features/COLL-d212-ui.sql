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

