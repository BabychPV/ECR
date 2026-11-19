-- B179:dependency-cycle ── ru/kz: відмова публікації версії шаблону, що замкнула б цикл залежностей документів (B179 L3); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.dependencyCycle', N'ru', N'Публикация отклонена: эта версия замкнула бы цикл зависимостей документов ({cycle}) — документ читал бы собственные данные. Уберите чтение справочника, который наполняет этот же шаблон.'),
    (N'err.ECR-TMPL-0422.dependencyCycle', N'kz', N'Жариялау қабылданбады: бұл нұсқа құжаттар тәуелділігінің циклін тұйықтар еді ({cycle}) — құжат өз деректерін оқитын болады. Осы үлгінің өзі толтыратын анықтамалықты оқуды алып тастаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B179:dependency-cycle ── кінець секції ──

-- B179:dependency-cycle ── en: відмова публікації на цикл залежностей; ru/kz — порцією B179:dependency-cycle вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.dependencyCycle', N'en', N'Publication was rejected: this version would close a document dependency cycle ({cycle}), so a document would read its own data. Remove the read of the registry that this same template fills.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B179:dependency-cycle ── кінець секції ──
