-- COLL:pm-nae-restore-purge-ui ── ru/kz: картка закритого вузла НАЕ - Restore / Delete for good (людина 04.11); нові види посилань; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.nae.lifecycle.retiredNote', N'ru', N'Узел закрыт. Его можно восстановить или, если он нигде не используется, удалить окончательно.'),
    (N'permits.nae.lifecycle.retiredNote', N'kz', N'Түйін жабылған. Оны қалпына келтіруге немесе еш жерде қолданылмаса, біржола жоюға болады.'),
    (N'permits.nae.lifecycle.restore', N'ru', N'Восстановить'),
    (N'permits.nae.lifecycle.restore', N'kz', N'Қалпына келтіру'),
    (N'permits.nae.lifecycle.restoreWithParents', N'ru', N'Восстановить вместе с предками ({count})'),
    (N'permits.nae.lifecycle.restoreWithParents', N'kz', N'Ата-түйіндерімен бірге қалпына келтіру ({count})'),
    (N'permits.nae.lifecycle.restored', N'ru', N'Восстановлено узлов: {count}.'),
    (N'permits.nae.lifecycle.restored', N'kz', N'Қалпына келтірілген түйіндер: {count}.'),
    (N'permits.nae.lifecycle.delete', N'ru', N'Удалить окончательно'),
    (N'permits.nae.lifecycle.delete', N'kz', N'Біржола жою'),
    (N'permits.nae.lifecycle.deleteTitle', N'ru', N'Удалить «{name}» окончательно?'),
    (N'permits.nae.lifecycle.deleteTitle', N'kz', N'«{name}» біржола жойылсын ба?'),
    (N'permits.nae.lifecycle.deleteText', N'ru', N'Узел и его атрибуты будут удалены безвозвратно. Это действие нельзя отменить.'),
    (N'permits.nae.lifecycle.deleteText', N'kz', N'Түйін мен оның атрибуттары қайтарусыз жойылады. Бұл әрекетті болдырмау мүмкін емес.'),
    (N'permits.nae.lifecycle.deleted', N'ru', N'Узел удален окончательно.'),
    (N'permits.nae.lifecycle.deleted', N'kz', N'Түйін біржола жойылды.'),
    (N'registries.referenceKind.sourceEventValueMaps', N'ru', N'Сопоставления значений событий источников'),
    (N'registries.referenceKind.sourceEventValueMaps', N'kz', N'Дереккөз оқиғалары мәндерінің сәйкестіктері'),
    (N'registries.referenceKind.snapshots', N'ru', N'Поданные срезы'),
    (N'registries.referenceKind.snapshots', N'kz', N'Берілген кесінділер'),
    (N'registries.referenceKind.archivedCells', N'ru', N'Ячейки в архиве'),
    (N'registries.referenceKind.archivedCells', N'kz', N'Мұрағаттағы ұяшықтар'),
    (N'registries.referenceKind.matchRules', N'ru', N'Правила сопоставления методик'),
    (N'registries.referenceKind.matchRules', N'kz', N'Әдістемелердің сәйкестендіру ережелері'),
    (N'registries.referenceKind.other', N'ru', N'Другие ссылки'),
    (N'registries.referenceKind.other', N'kz', N'Басқа сілтемелер')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:pm-nae-restore-purge-ui ── кінець секції ──

-- COLL:pm-nae-restore-purge-ui ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.nae.lifecycle.retiredNote', N'en', N'This node is retired. You can restore it or, if it is not used anywhere, delete it for good.', 1),
    (N'permits.nae.lifecycle.restore', N'en', N'Restore', 1),
    (N'permits.nae.lifecycle.restoreWithParents', N'en', N'Restore with parents ({count})', 1),
    (N'permits.nae.lifecycle.restored', N'en', N'Nodes restored: {count}.', 1),
    (N'permits.nae.lifecycle.delete', N'en', N'Delete for good', 1),
    (N'permits.nae.lifecycle.deleteTitle', N'en', N'Delete «{name}» for good?', 1),
    (N'permits.nae.lifecycle.deleteText', N'en', N'The node and its attributes are removed permanently. This cannot be undone.', 1),
    (N'permits.nae.lifecycle.deleted', N'en', N'The node is deleted for good.', 1),
    (N'registries.referenceKind.sourceEventValueMaps', N'en', N'Source event value maps', 1),
    (N'registries.referenceKind.snapshots', N'en', N'Submitted snapshots', 1),
    (N'registries.referenceKind.archivedCells', N'en', N'Archived cells', 1),
    (N'registries.referenceKind.matchRules', N'en', N'Methodology match rules', 1),
    (N'registries.referenceKind.other', N'en', N'Other references', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:pm-nae-restore-purge-ui ── кінець секції ──
