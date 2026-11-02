-- COLL:rt25 ── ru/kz перерахунку документів, зачеплених правкою довідника (RT-25); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.kind.registryImpactRecalculation', N'ru', N'Пересчёт документов, затронутых правкой справочника'),
    (N'jobs.registryImpactReading', N'ru', N'Поиск документов, затронутых справочником'),
    (N'jobs.registryImpactDone', N'ru', N'Поставлено на пересчёт: {queued}; пропущено: {skipped}; больше не затронуто: {gone}'),
    (N'err.ECR-REQ-0422.impactReasonRequired', N'ru', N'Для пересчёта затронутых документов нужна причина.'),
    (N'err.ECR-REQ-0422.impactReasonTooLong', N'ru', N'Причина длиннее {max} символов.'),
    (N'err.ECR-REG-0422.impactDocumentNotAffected', N'ru', N'Документ {documentId} не входит в документы открытых периодов, затронутые этим справочником.'),
    (N'err.ECR-REG-0422.impactNothingToRecalculate', N'ru', N'Нет затронутых документов открытых периодов, которые можно пересчитать.'),
    (N'jobs.kind.registryImpactRecalculation', N'kz', N'Анықтамалықты өзгерту әсер еткен құжаттарды қайта есептеу'),
    (N'jobs.registryImpactReading', N'kz', N'Анықтамалыққа байланысты құжаттарды іздеу'),
    (N'jobs.registryImpactDone', N'kz', N'Қайта есептеуге қойылды: {queued}; өткізілді: {skipped}; енді әсер етпейді: {gone}'),
    (N'err.ECR-REQ-0422.impactReasonRequired', N'kz', N'Әсер еткен құжаттарды қайта есептеу үшін себеп қажет.'),
    (N'err.ECR-REQ-0422.impactReasonTooLong', N'kz', N'Себеп {max} таңбадан ұзын.'),
    (N'err.ECR-REG-0422.impactDocumentNotAffected', N'kz', N'{documentId} құжаты осы анықтамалық әсер еткен ашық кезеңдердің құжаттарына кірмейді.'),
    (N'err.ECR-REG-0422.impactNothingToRecalculate', N'kz', N'Қайта есептеуге болатын, әсер еткен ашық кезең құжаттары жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rt25 ── кінець секції ──

-- COLL:rt25 ── перерахунок документів, зачеплених правкою довідника (RT-25) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.kind.registryImpactRecalculation', N'en', N'Recalculation of documents affected by a registry edit', 1),
    (N'jobs.registryImpactReading', N'en', N'Finding documents affected by the registry', 1),
    (N'jobs.registryImpactDone', N'en', N'Queued for recalculation: {queued}; skipped: {skipped}; no longer affected: {gone}', 1),
    (N'err.ECR-REQ-0422.impactReasonRequired', N'en', N'Recalculating affected documents requires a reason.', 1),
    (N'err.ECR-REQ-0422.impactReasonTooLong', N'en', N'The reason is longer than {max} characters.', 1),
    (N'err.ECR-REG-0422.impactDocumentNotAffected', N'en', N'Document {documentId} is not among the open-period documents affected by this registry.', 1),
    (N'err.ECR-REG-0422.impactNothingToRecalculate', N'en', N'There are no affected open-period documents that can be recalculated.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rt25 ── кінець секції ──
