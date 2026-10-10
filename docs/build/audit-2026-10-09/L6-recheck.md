# L6 recheck @3a3818ef (agent ab5e92b3)
FIXED: 01 (b238cb87 refuse preview >MaxChanges), 04, 05, 06, 07 (D-278), 08, 09, 10, 11, 12, 14, 15(caveat). L6-03 N/A D-280 (code matches PatchCellsHandler.cs:1097-1104).
L6-02 PARTIAL: writers without doc-structure lock:
 - RowStore.EnsureTableInstancesAsync (RowStore.cs:690-797, outside txn; GET tables/export/import preview/MaterializationTargets) -> during migration window inserts V1 TableInstances -> ECR-TMPL-0404 + duplicated structure. Fix: own txn if none (execution strategy), EnterStructureAsync(S) BEFORE reading DocumentSheets; keep UQ catch.
 - CreateDocumentHandler (:243, 160-196) reads project version unlocked -> doc with V1 sheets under V2 project. Fix: ExecuteInTransactionAsync + LockProjectTemplateVersionAsync UPDLOCK,HOLDLOCK,ROWLOCK; mismatch -> ConcurrencyConflictException SheetBusy structureChanged.
 - SourceEventSyncJob delete (:873-893, 943-975) raw sheet-edit only; take doc-structure S before TryLockSheetAsync, re-read sheetDefId.
 - Import low: ImportPlan (ExcelImporter.cs:866) lacks TemplateVersionId; add, set in PreviewAsync, EnsureUnchanged in ApplyAsync.
 - Approve/Return/Recall/Reopen no lock: safe (migration refuses sheetsLocked). DeleteDocument: deadlock only, retried.
 Race test only for PATCH (DocumentVersionMigrationTests.Race.cs:44). Test for Ensure: decorator pauses migration after ApplyAsync; GET /tables waits on doc-struc; instances only V2.
L6-13 PARTIAL: PatchCellsResponse.cs:6 doc says "hex", actually Base64 (NormalizedCellStore.cs:764,1139) -> fix doc + openapi snapshot + schema.d.ts.
L6-15 caveat: UnitOfWork.cs:450-484 detaches only new entities; entity loaded before closure, modified, SaveChanges succeeded then 1205 later -> retry silently skips write. Fix: SaveChangesAsync(acceptAllChangesOnSuccess:false) in retried txn + AcceptAllChanges after commit; test interceptor SavedChangesAsync throws after success.
L6-14: RecalculationService.cs:888 CorrelationId null (bg job, out of scope).
