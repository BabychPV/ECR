-- SHEETNAME:sheet-name ── редактор виразів: опис функції SHEETNAME() (назва екземпляра аркуша) ── ru/kz; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'expressions.fn.sheetname', N'ru', N'Название листа, на котором вычисляется формула; у копии листа — «<Название> N». Пересчитывается при переименовании.'),
    (N'expressions.fn.sheetname', N'kz', N'Формула есептелетін парақтың атауы; парақ көшірмесінде — «<Атауы> N». Атауы өзгергенде қайта есептеледі.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- SHEETNAME:sheet-name ── кінець секції ──
-- SHEETNAME:sheet-name ── редактор виразів: опис функції SHEETNAME() ── en; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'expressions.fn.sheetname', N'en', N'Name of the sheet the formula is calculated on; on a sheet copy — "<Name> N". Recalculated on rename.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- SHEETNAME:sheet-name ── кінець секції ──
