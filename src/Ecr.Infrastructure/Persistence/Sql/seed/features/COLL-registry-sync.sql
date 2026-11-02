-- COLL:registry-sync ── Дубль ключа між записами одного пакета `RegistryEntryWriter` (синк довідника, S7-3; FEATURE-REGISTRY-TABLES §4.3 крок 3) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-4092.keyDuplicateInBatch',                   N'en', N'Another entry written in the same batch would get the same key.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:registry-sync ── кінець секції ──
