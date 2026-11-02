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
