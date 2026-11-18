# MS T4 (ms-web): рядки інтерфейсу `methodologyScript.*`

Усі ключі, які просить клієнт у `src/Ecr.Web/src/features/methodologyScript/`. Для сіду (`en`; `ru`/`kz` — за зразком). `{…}` — параметри.

Також для `EndpointCoverageTests.DynamicKeySites`: `scriptMarkers.ts` викликає `t(diagnostic.messageKey, …)` (ключі `script.*` із сервера) — потрібен запис
`new("features/methodologyScript/scriptMarkers.ts", "diagnostic.messageKey", 1, null, [], "messageKey діагностики скрипта методології.")`.

| key | en |
|---|---|
| methodologyScript.editorLoadFailed | The script editor could not be loaded. Reload the page and try again. |
| methodologyScript.problems.title | Problems |
| methodologyScript.problems.none | No problems found. |
| methodologyScript.problems.jump | Go to line {line}, column {column} |
| methodologyScript.outline.inputs | Reads (inputs) |
| methodologyScript.outline.outputs | Writes (outputs) |
| methodologyScript.outline.tables | Tables |
| methodologyScript.outline.imports | Imports |
| methodologyScript.outline.noneItems | None |
| methodologyScript.outline.optional | optional |
| methodologyScript.outline.perSubstance | per substance |
| methodologyScript.outline.noColumn | no column |
| methodologyScript.preview.empty | Nothing to show for this document and period. |
| methodologyScript.preview.summary | {total} rows, {selected} selected, {inactive} inactive |
| methodologyScript.preview.col.row | Row |
| methodologyScript.preview.col.state | State |
| methodologyScript.preview.col.issue | Issue |
| methodologyScript.preview.state.selected | Selected |
| methodologyScript.preview.state.inactive | Inactive |
| methodologyScript.preview.state.notSelected | Not selected |
| methodologyScript.preview.state.rejected | Rejected |
| methodologyScript.preview.issue.missingInputs | Missing inputs |
| methodologyScript.preview.issue.stepError | Calculation error |
| methodologyScript.preview.issue.rejected | Rejected by the script |
| methodologyScript.preview.expandRow | Show all values of row {row} |
| methodologyScript.preview.collapseRow | Hide values of row {row} |
| methodologyScript.preview.createTest | Create test from this row |
| methodologyScript.constTable.title | Edit constant as a table |
| methodologyScript.constTable.titleNamed | Edit constant {name} as a table |
| methodologyScript.constTable.singleColumn | Single column (one-level map) |
| methodologyScript.constTable.key | Key |
| methodologyScript.constTable.value | Value |
| methodologyScript.constTable.defaultColumn | Value |
| methodologyScript.constTable.columnKey | Column {n} key |
| methodologyScript.constTable.rowKey | Row {n} key |
| methodologyScript.constTable.cell | Value for {row} / {column} |
| methodologyScript.constTable.removeRow | Remove row {key} |
| methodologyScript.constTable.removeColumn | Remove column {key} |
| methodologyScript.constTable.addRow | Add row |
| methodologyScript.constTable.addColumn | Add column |
| methodologyScript.constTable.pasteLabel | Paste a table from Excel |
| methodologyScript.constTable.pasteHint | Copy cells in Excel and paste here. You can also paste directly into a cell. Decimal comma is accepted. |
| methodologyScript.constTable.firstRowHeaders | First row holds column names |
| methodologyScript.constTable.replaceFromPaste | Replace table with pasted data |
| methodologyScript.constTable.fixKeys | Keys must be non-empty and unique. |
| methodologyScript.constTable.fixCells | Fix the marked cells: a number with a thousands separator is ambiguous. |
| methodologyScript.constTable.commentsLost | Apply will remove {n} comment(s) (they were inside changed or deleted entries). Comments on untouched entries are kept. |
| methodologyScript.constTable.error.ambiguousNumber | Ambiguous number: a comma or dot may be a thousands separator or a decimal mark. Write it without a thousands separator, or quote it as text. |
| methodologyScript.constTable.cancel | Cancel |
| methodologyScript.constTable.apply | Apply |
| methodologyScript.constTable.error.emptyKey | Key is empty |
| methodologyScript.constTable.error.duplicateKey | Duplicate key |
| methodologyScript.constTable.error.notMap | This constant is not a map and cannot be edited as a table. |
| methodologyScript.constTable.error.mixedLevels | The map mixes values and nested maps; edit it as text. |
| methodologyScript.constTable.error.tooDeep | The map has more than two levels; edit it as text. |
| methodologyScript.constTable.error.syntax | The constant cannot be parsed; fix the syntax in the text first. |
