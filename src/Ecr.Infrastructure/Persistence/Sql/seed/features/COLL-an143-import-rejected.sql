-- COLL:an143-import-rejected ── ru/kz: AN-143 (p53): застосування імпорту .xlsx відмовляє цілком, якщо перегляд має відхилені комірки; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-IMP-0422.importHasRejected', N'ru', N'В предпросмотре отклонённых ячеек: {rejectedCount}, поэтому импорт отклонён целиком и ничего не применено. Исправьте или очистите отклонённые ячейки в файле либо выгрузите книгу заново и постройте предпросмотр снова.'),
    (N'err.ECR-IMP-0422.importHasRejected', N'kz', N'Алдын ала қарауда қабылданбаған ұяшықтар: {rejectedCount}, сондықтан импорт толығымен қабылданбады және ештеңе қолданылмады. Файлдағы қабылданбаған ұяшықтарды түзетіңіз немесе тазалаңыз не кітапты қайта экспорттап, алдын ала қарауды қайта құрыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an143-import-rejected ── кінець секції ──

-- COLL:an143-import-rejected ── AN-143 (p53 «все або нічого»): застосування імпорту .xlsx відмовляє цілком, якщо перегляд має відхилені комірки; ru/kz - порцією COLL:an143-import-rejected нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-IMP-0422.importHasRejected', N'en', N'The preview has {rejectedCount} rejected cell(s), so the import was refused as a whole and nothing was applied. Fix or clear the rejected cells in the file, or export the workbook again, and build the preview again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an143-import-rejected ── кінець секції ──
