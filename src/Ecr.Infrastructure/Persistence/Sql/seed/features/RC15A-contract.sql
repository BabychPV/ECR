-- RC15-A:contract ── ru/kz: секція «Contract» шапки документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.header.contract.title', N'ru', N'Контракт'),
    (N'document.header.contract.title', N'kz', N'Келісімшарт'),
    (N'document.header.contract.fileNumber', N'ru', N'File Number — Номер файла'),
    (N'document.header.contract.fileNumber', N'kz', N'File Number — Файл нөмірі'),
    (N'document.header.contract.version', N'ru', N'Version — Версия'),
    (N'document.header.contract.version', N'kz', N'Version — Нұсқа')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- RC15-A:contract ── кінець секції ──
-- RC15-A:contract ── секція «Contract» шапки документа (en); ru/kz — порцією RC15-A:contract у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.header.contract.title', N'en', N'Contract', 1),
    (N'document.header.contract.fileNumber', N'en', N'File Number — Номер файла', 1),
    (N'document.header.contract.version', N'en', N'Version — Версия', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- RC15-A:contract ── кінець секції ──
