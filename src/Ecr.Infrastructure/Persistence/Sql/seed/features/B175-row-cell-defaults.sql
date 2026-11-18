-- B175:row-cell-defaults ── ru/kz: відмови дефолтів комірок рядка (SaveRowDef: невідома колонка, обчислювана/Unit колонка, невідомий код довідника, непридатне значення; публікація: дефолт на колонку, якої вже немає); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.rowDefaultUnknownColumn', N'ru', N'Значение по умолчанию строки «{rowKey}»: колонки «{columnCode}» в этой таблице нет.'),
    (N'err.ECR-TMPL-0422.rowDefaultUnknownColumn', N'kz', N'«{rowKey}» жолының әдепкі мәні: бұл кестеде «{columnCode}» бағаны жоқ.'),
    (N'err.ECR-TMPL-0422.rowDefaultColumnNotInput', N'ru', N'Значение по умолчанию строки «{rowKey}»: колонка «{columnCode}» вычисляемая или хранит единицу измерения и не принимает значение по умолчанию.'),
    (N'err.ECR-TMPL-0422.rowDefaultColumnNotInput', N'kz', N'«{rowKey}» жолының әдепкі мәні: «{columnCode}» бағаны есептелетін немесе өлшем бірлігін сақтайды және әдепкі мәнді қабылдамайды.'),
    (N'err.ECR-TMPL-0422.rowDefaultLookupCodeUnknown', N'ru', N'Значение по умолчанию строки «{rowKey}»: в справочнике колонки «{columnCode}» нет записи с кодом «{value}».'),
    (N'err.ECR-TMPL-0422.rowDefaultLookupCodeUnknown', N'kz', N'«{rowKey}» жолының әдепкі мәні: «{columnCode}» бағанының анықтамалығында «{value}» кодты жазба жоқ.'),
    (N'err.ECR-TMPL-0422.rowDefaultValueInvalid', N'ru', N'Значение по умолчанию строки «{rowKey}»: «{value}» не подходит по типу колонки «{columnCode}».'),
    (N'err.ECR-TMPL-0422.rowDefaultValueInvalid', N'kz', N'«{rowKey}» жолының әдепкі мәні: «{value}» мәні «{columnCode}» бағанының түріне сәйкес емес.'),
    (N'err.ECR-TMPL-0422.rowDefaultColumnMissing', N'ru', N'Версию нельзя опубликовать: значение по умолчанию строки «{rowKey}» таблицы {tableCode} ссылается на колонку «{columnCode}», которой больше нет или которая не принимает значение по умолчанию. Исправьте значения по умолчанию строки.'),
    (N'err.ECR-TMPL-0422.rowDefaultColumnMissing', N'kz', N'Нұсқаны жариялауға болмайды: {tableCode} кестесінің «{rowKey}» жолының әдепкі мәні жоқ немесе әдепкі мәнді қабылдамайтын «{columnCode}» бағанына сілтейді. Жолдың әдепкі мәндерін түзетіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B175:row-cell-defaults ── кінець секції ──

-- B175:row-cell-defaults ── en-порція ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.rowDefaultUnknownColumn', N'en', N'Default of row "{rowKey}": there is no column "{columnCode}" in this table.', 1),
    (N'err.ECR-TMPL-0422.rowDefaultColumnNotInput', N'en', N'Default of row "{rowKey}": column "{columnCode}" is computed or holds a unit and does not take a default value.', 1),
    (N'err.ECR-TMPL-0422.rowDefaultLookupCodeUnknown', N'en', N'Default of row "{rowKey}": the registry of column "{columnCode}" has no entry with code "{value}".', 1),
    (N'err.ECR-TMPL-0422.rowDefaultValueInvalid', N'en', N'Default of row "{rowKey}": "{value}" does not fit the type of column "{columnCode}".', 1),
    (N'err.ECR-TMPL-0422.rowDefaultColumnMissing', N'en', N'The version cannot be published: the default of row "{rowKey}" in table {tableCode} refers to column "{columnCode}", which no longer exists or does not take a default value. Fix the row defaults.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B175:row-cell-defaults ── кінець секції ──
