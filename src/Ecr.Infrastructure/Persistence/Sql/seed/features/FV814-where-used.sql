-- FV814:where-used ── ФВ-8.14 «Де використовується»: правила валідації, правила довідників, дані колонки ── ru/kz; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'usageKind.validationRule', N'ru', N'Правило проверки шаблона'),
    (N'usageKind.validationRule', N'kz', N'Үлгіні тексеру ережесі'),
    (N'usageKind.registryRule', N'ru', N'Правило справочника'),
    (N'usageKind.registryRule', N'kz', N'Анықтамалық ережесі'),
    (N'columns.usageDataInDocuments', N'ru', N'Значения этого столбца уже хранятся в документах'),
    (N'columns.usageDataInDocuments', N'kz', N'Бұл бағанның мәндері құжаттарда бұрыннан сақталған')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- FV814:where-used ── кінець секції ──
-- FV814:where-used ── ФВ-8.14 «Де використовується» ── en; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'usageKind.validationRule', N'en', N'Template validation rule', 1),
    (N'usageKind.registryRule', N'en', N'Registry rule', 1),
    (N'columns.usageDataInDocuments', N'en', N'Values of this column are already stored in documents', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- FV814:where-used ── кінець секції ──
-- FV814:entry-documents ── ФВ-8.14: документи, де стоїть запис довідника (EntryUsageModal) ── ru/kz; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'usageKind.document', N'ru', N'Документ'),
    (N'usageKind.document', N'kz', N'Құжат'),
    (N'registries.entryUsage.documentsTitle', N'ru', N'Документы, содержащие эту запись'),
    (N'registries.entryUsage.documentsTitle', N'kz', N'Осы жазбаны қамтитын құжаттар'),
    (N'registries.entryUsage.documentsHint', N'ru', N'Перечислены только документы, которые вы можете открыть; значения в удалённых строках не учитываются.'),
    (N'registries.entryUsage.documentsHint', N'kz', N'Тек сіз аша алатын құжаттар тізілген; жойылған жолдардағы мәндер есептелмейді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- FV814:entry-documents ── кінець секції ──
-- FV814:entry-documents ── en; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'usageKind.document', N'en', N'Document', 1),
    (N'registries.entryUsage.documentsTitle', N'en', N'Documents that hold this entry', 1),
    (N'registries.entryUsage.documentsHint', N'en', N'Only documents you can open are listed; values in deleted rows are not counted.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- FV814:entry-documents ── кінець секції ──
-- FV814:entry-documents ── змінені тексти наявних ключів: комірки й шапки документів тепер перелічено на запис
-- (розділ документів), тож «не перелічується» про них — неправда. Епілог оновлює, лише поки в базі стоїть САМЕ старе значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'registries.entryUsage.notListed', N'en', N'Not listed here per entry: document cells and headers, methodology constants and cascade links. Deleting the entry reports how many of them there are, by kind.', N'Not listed here per entry: methodology constants and cascade links. Deleting the entry reports how many of them there are, by kind.'),
    (N'registries.entryUsage.notListed', N'ru', N'Здесь для отдельной записи не перечисляются: ячейки и шапки документов, константы методик и связи каскада. Удаление записи сообщает, сколько их, по видам.', N'Здесь для отдельной записи не перечисляются: константы методик и связи каскада. Удаление записи сообщает, сколько их, по видам.'),
    (N'registries.entryUsage.notListed', N'kz', N'Мұнда жеке жазба үшін тізілмейді: құжат ұяшықтары мен тақырыптары, әдістеме константалары және каскад байланыстары. Жазбаны жою олардың қанша екенін түрлері бойынша хабарлайды.', N'Мұнда жеке жазба үшін тізілмейді: әдістеме константалары және каскад байланыстары. Жазбаны жою олардың қанша екенін түрлері бойынша хабарлайды.'),
    (N'registries.entryUsage.columnsHint', N'en', N'These columns can hold this entry; which documents actually hold it is not listed per entry.', N'These columns can hold this entry; the documents that actually hold it are listed above.'),
    (N'registries.entryUsage.columnsHint', N'ru', N'Эти столбцы могут содержать эту запись; какие документы её действительно содержат, для отдельной записи не перечисляется.', N'Эти столбцы могут содержать эту запись; документы, которые её действительно содержат, перечислены выше.'),
    (N'registries.entryUsage.columnsHint', N'kz', N'Бұл бағандарда осы жазба болуы мүмкін; оны нақты қай құжаттар қамтитыны жеке жазба үшін тізілмейді.', N'Бұл бағандарда осы жазба болуы мүмкін; оны нақты қамтитын құжаттар жоғарыда тізілген.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- FV814:entry-documents ── кінець секції ──
