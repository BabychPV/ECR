-- COLL:pm-import-package-rejected ── ru/kz: PM-UI-IMPORT-REJECT-BANNER: банер «пакет імпорту відхилено» у перегляді; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'import.preview.packageRejected', N'ru', N'Пакет импорта отклонён целиком'),
    (N'import.preview.packageRejected', N'kz', N'Импорт пакеті толығымен қабылданбады'),
    (N'import.preview.packageRejectedHint', N'ru', N'Состояние периода месячной колонки (закрыт, подан или утверждён) запрещает изменения в колонках: {columns}. Импорт применяется по принципу «всё или ничего», поэтому ни одно значение не будет записано. Уберите изменения в этих колонках в файле либо выгрузите книгу заново и постройте предпросмотр снова.'),
    (N'import.preview.packageRejectedHint', N'kz', N'Айлық бағанның кезең күйі (жабық, жіберілген немесе бекітілген) мына бағандардағы өзгерістерге тыйым салады: {columns}. Импорт «не бәрі, не ештеңе» қағидатымен қолданылады, сондықтан бірде-бір мән жазылмайды. Файлдағы осы бағандардағы өзгерістерді алып тастаңыз не кітапты қайта экспорттап, алдын ала қарауды қайта құрыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:pm-import-package-rejected ── кінець секції ──

-- COLL:pm-import-package-rejected ── PM-UI-IMPORT-REJECT-BANNER: en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'import.preview.packageRejected', N'en', N'The import package is rejected as a whole', 1),
    (N'import.preview.packageRejectedHint', N'en', N'The period state of a month column (closed, submitted or approved) forbids changes in the columns: {columns}. The import is all-or-nothing, so no value will be written. Remove the changes in these columns from the file, or export the workbook again and build the preview again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:pm-import-package-rejected ── кінець секції ──
