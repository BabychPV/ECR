# Ready: lane/integ-rpt-versions3

- Branch: `lane/integ-rpt-versions3` (linear, 12 commits over main)
- Head SHA: `c9a5cbd1802bc17ed8824d7d5a9a353a1ad76ff7`
- Base main SHA: `c8a9cfb796d255f623fcf37cfacfcfbcb9501c38`
- CI: run 41354055111 — success (full)
- Evidence: run 41354055195 — success
- Migration: YES — `20261101211255_RPTV01_ProjectVersionTimeline` (doc.ProjectTemplateVersion, cfg.ColumnRename, doc.fn_VersionOf, first-row data seed). Parent: `20261031142719_D465SourceEventAttributeValues`. has-pending-model-changes clean.
- Contract changed: YES (Part B contract, unchanged since RC p88): migrate-version `range`/`fromPeriodKey` + response `effectiveFromPeriodKey`/`warnings`, `GET /projects/{id}/template-versions`, `fromMonthEnabled` in `GET /documents/{id}/migrate-version`, `templateVersionByPeriod` filled. openapi + schema.d.ts regenerated last, byte-identical.
- FromMonth flag `Documents:VersionMigration:FromMonthEnabled`: default OFF.

## Contents
- Part B (cherry-picked from rpt-versions2 / RC p88), RPTV01 regenerated after D465.
- B2: period-aware readers (approval queue, returned sheets, current approval step, archive reader, consistency reader, report-snapshot staleness, submission target, approval snapshot refresh, validation result), period-aware structure gate in styles / workbook / submit / Excel import, HasSheet by code; guard Debt list = 0 (remaining reads justified as Current).
- D-461 fix: FromMonth no longer releases manual overrides of periods before X (bug found; test + mutation proof).

## Risks / notes
- Mixed-owner Q-17 routed read across versions not tested: the January rule keeps a year's family on one version; the router degrades to NULL if violated.
- Edit-assignment validity, header and permit-binding paths stay doc-level (current version; grants are copied to new ids by the migration).
- Flag ON is reasonable after this RC is merged and one live window confirms submit/export/recalc of an old period on EcrLandUi.
