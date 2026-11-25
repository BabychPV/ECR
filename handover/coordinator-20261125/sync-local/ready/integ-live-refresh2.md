# Ready: lane/integ-live-refresh2

- Branch: lane/integ-live-refresh2
- Head SHA: b1eb63e30f4c09f4a433966cf8b1885a9a882746 (1 commit, clean cherry-pick of 395bf0961)
- Base: origin/main c8a9cfb79
- CI: run 41353840133 success; Evidence: run 41353840095 success
- Migration: no
- Contract changed: yes (GET /documents/{id}/sheets/{sheetDefId}/changes; revision + ETag/304 in discussion summary). openapi.snapshot.json and schema.d.ts regenerate byte-identical on the new base (openapi-typescript 7.5.0).
- Notes: no query-count/perf ceilings touched or conflicting. Temporary EndpointCoverage exemption for GET .../changes stays until the UI consumer (Permites) lands. Local: Architecture 1169/1169; Api SheetChanges/TableDiscussion/OpenApiSnapshot/EndpointCoverage 19/19; Infrastructure SheetChange 2/2; Application SheetChange 9/9.
