---
name: f13-grep-exception-message-leak-1113
description: "Every F13 (and my own code) — grep the diff for ex.Message / exception.ToString() reaching DTOs, health cards, ProblemDetails, user text (retro rule 368)"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-11-13T12:37:55.087Z
---

In every F13 review and before pushing my own code: grep the diff for `ex.Message`, `exception.Message`, `.ToString()` of an exception that reaches a response DTO, a health card (`IReportViewStatus.Failed`, health descriptions), ProblemDetails or any user-facing text. Only a fixed text + `SafeErrorText.For(...)` / a correlation id is allowed; the full exception goes to the log under the same correlation.

**Why:** /health/ready is ANONYMOUS and HealthResponse redacts only the "db" card. 13.11 my own P-4b (#859) wrote raw exception text to the `reportviews` card (PublishTemplateVersionHandler, ReportViewsJob); I caught the same class only later in PE's #872 delta. Coordinator retro rule 368.

**How to apply:** add this grep line to every F13 brief for subagents and to my pre-push checklist. Related: [[no-full-ci-for-mutations-1113]].
