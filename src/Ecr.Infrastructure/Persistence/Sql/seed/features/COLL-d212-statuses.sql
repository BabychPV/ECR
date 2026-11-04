-- COLL:d212-statuses ── ru/kz статусів подій синку довідника (D-212 PR-3); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'status.coverage.RegistryAutoCreated', N'ru', N'Создано из источника'),
    (N'status.coverage.RegistryAutoCreated', N'kz', N'Деректер көзінен құрылды'),
    (N'status.coverage.RegistryDeactivated', N'ru', N'Отключено: отсутствует в источнике'),
    (N'status.coverage.RegistryDeactivated', N'kz', N'Өшірілді: деректер көзінде жоқ'),
    (N'status.coverage.RegistryReactivated', N'ru', N'Снова включено: вернулось в источник'),
    (N'status.coverage.RegistryReactivated', N'kz', N'Қайта қосылды: деректер көзіне оралды'),
    (N'status.coverage.RegistryRuleViolation', N'ru', N'Нарушает правило справочника'),
    (N'status.coverage.RegistryRuleViolation', N'kz', N'Анықтамалық ережесін бұзады'),
    (N'status.coverage.RegistryExternalKeyRelinked', N'ru', N'Перепривязано по пути в источнике'),
    (N'status.coverage.RegistryExternalKeyRelinked', N'kz', N'Деректер көзіндегі жол бойынша қайта байланыстырылды'),
    -- Прогалину виявив сторож `Кожен_статус_покриття_має_підпис_en_ru_kz`: en є з HSE301 F4e, ru/kz не було.
    (N'status.coverage.SourceDataRefused', N'ru', N'Источник отказал в выдаче данных'),
    (N'status.coverage.SourceDataRefused', N'kz', N'Деректер көзі деректерді беруден бас тартты')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d212-statuses ── кінець секції ──

-- COLL:d212-statuses ── Статуси подій синку довідника з AF (D-212 PR-3, `CollectionCoverage.RegistryStatuses`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'status.coverage.RegistryAutoCreated',        N'en', N'Created from source', 1),
    (N'status.coverage.RegistryDeactivated',        N'en', N'Deactivated: missing in source', 1),
    (N'status.coverage.RegistryReactivated',        N'en', N'Reactivated: back in source', 1),
    (N'status.coverage.RegistryRuleViolation',      N'en', N'Violates a registry rule', 1),
    (N'status.coverage.RegistryExternalKeyRelinked', N'en', N'Relinked by source path', 1)
    -- ru/kz — окремою порцією `COLL:d212-statuses` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d212-statuses ── кінець секції ──
