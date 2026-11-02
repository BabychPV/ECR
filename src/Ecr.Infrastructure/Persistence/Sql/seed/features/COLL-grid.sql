-- COLL:grid ── Вставка чисел за роздільниками мови інтерфейсу (`clipboard.ts`, `readNumber`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- ⛔ `1,234` в англійському інтерфейсі — і тисяча, і одна ціла: не вгадується.
    (N'grid.pasteAmbiguousNumber',       N'en', N'"{value}" can be read as {asGroup} or as {asDecimal}. Paste it without thousands separators so the number is unambiguous.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:grid ── кінець секції ──
