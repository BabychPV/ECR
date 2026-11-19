-- B80:stale-approved-dependent ── ru/kz: B80 N4 (R3.2), знахідка нічної перевірки STALE_APPROVED_DEPENDENT — затверджений місяць споживача, чиє джерело змінилося; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'consistency.finding.STALE_APPROVED_DEPENDENT', N'ru', N'Утверждённый период {periodKey} документа {documentId} рассчитан из месяца {sourcePeriodKey} документа-источника {sourceDocumentId}, который после этого утверждения изменился: он больше не утверждён или утверждён повторно позже. Утверждённый период не пересчитывается — чтобы применить данные источника, верните период в работу и пересчитайте его.'),
    (N'consistency.finding.STALE_APPROVED_DEPENDENT', N'kz', N'{documentId} құжатының бекітілген {periodKey} кезеңі {sourceDocumentId} бастапқы құжатының {sourcePeriodKey} айынан есептелген, ал ол осы бекітуден кейін өзгерді: енді бекітілмеген немесе кейінірек қайта бекітілген. Бекітілген кезең қайта есептелмейді — бастапқы деректерді қолдану үшін кезеңді жұмысқа қайтарып, қайта есептеңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B80:stale-approved-dependent ── кінець секції ──

-- B80:stale-approved-dependent ── en (область 1 — адмінський екран /admin/consistency-issues); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'consistency.finding.STALE_APPROVED_DEPENDENT', N'en', N'Approved period {periodKey} of document {documentId} was calculated from month {sourcePeriodKey} of source document {sourceDocumentId}, which changed after that approval: it is no longer approved or was approved again later. An approved period is never recalculated; to apply the source data, reopen the period and recalculate it.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B80:stale-approved-dependent ── кінець секції ──
