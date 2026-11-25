---
name: pe-scan-table-before-data-source-question-1118
description: "Before asking the human \"where does data X come from?\", scan the target table/template rows AND columns for an existing value with the same meaning and list it as an option (P-0118-28)"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-18T14:50:53.552Z
---

Before putting a "where does data X come from?" question to the human (via the coordinator), scan the target table/template for existing ROWS and COLUMNS with the same meaning (row labels such as "Volume of …, m3", not just column codes) and list them as options in the question.

**Why:** B26 (18.11): I asked where the Volume for ECW_C09_02_01 on T7b_00 comes from after listing only column codes. The human chose (a), a manual column, and it was built. Only then did the subagent notice that row S3_9 "Volume of the water discharged to Caspian sea, m3" already held it. The human switched to S3_9, so the work was built twice (coordinator retro P-0118-28).

**How to apply:** when analysing missing inputs, read the row labels of the table (model json `rowValues`/RowDefs) as well as the column list. Also check sibling tables. Then put "existing row/column X" first among the options.
