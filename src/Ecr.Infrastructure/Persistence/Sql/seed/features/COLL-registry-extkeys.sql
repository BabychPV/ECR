-- COLL:registry-extkeys ── Зовнішні ідентифікатори запису довідника (ФВ-8.10, FEATURE-REGISTRY-SYNC S2): панель у формі запису і відмови API ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.externalKeys',                N'en', N'External identifiers', 1),
    (N'registries.externalKeysHint',            N'en', N'Links of this entry to elements of external sources: synchronization finds the entry by them.', 1),
    (N'registries.externalKeysEmpty',           N'en', N'This entry is not linked to any source element.', 1),
    (N'registries.externalKeySource',           N'en', N'Source', 1),
    (N'registries.externalKeyId',               N'en', N'Identifier in the source', 1),
    (N'registries.externalKeyPath',             N'en', N'Path in the source', 1),
    (N'registries.externalKeyAdd',              N'en', N'Link', 1),
    (N'registries.externalKeyAdded',            N'en', N'External identifier linked', 1),
    (N'registries.externalKeyRemove',           N'en', N'Unlink', 1),
    (N'registries.externalKeyRemoveTitle',      N'en', N'Unlink identifier "{externalId}"?', 1),
    (N'registries.externalKeyRemoveText',       N'en', N'Synchronization will no longer find this entry by this identifier.', 1),
    (N'registries.externalKeyRemoved',          N'en', N'External identifier unlinked', 1),
    (N'err.ECR-REQ-0422.externalKeyInvalid',    N'en', N'The external identifier is required and may be up to {max} characters.', 1),
    (N'err.ECR-REG-0409.externalKeyTaken',      N'en', N'Identifier "{externalId}" of source "{dataSource}" is already linked to entry "{code}".', 1),
    (N'err.ECR-REG-0409.externalKeyTakenConcurrently', N'en', N'Identifier "{externalId}" was just linked by another request.', 1),
    (N'err.ECR-REG-0404.externalKey',           N'en', N'External identifier {id} was not found in registry "{registryCode}".', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:registry-extkeys ── кінець секції ──
