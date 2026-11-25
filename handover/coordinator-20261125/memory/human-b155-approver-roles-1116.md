---
name: human-b155-approver-roles-1116
description: "B155 (16.11): general Approver for all templates; a custom template role is ADDED (OR), not a replacement; option C"
metadata:
  node_type: memory
  type: project
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-16T17:52:02.365Z
---

B155 (human quiz 16.11, via the coordinator). Option C: `wf.ApprovalRoute.TemplateId NULL`. Resolution, most specific first: version → (project, template) → (all projects, template) → project → site default.

**Human's answers:**
- The general built-in `Approver` approves ALL templates that have no own route.
- A custom template role is ADDED to the general one, NOT a replacement. A step of a template route is satisfied by its custom role OR by the general Approver.
- Tests must show: a custom-only holder approves; a general-only holder approves; neither is refused; a multi-step route keeps its order.
- A template route applies in all projects, with a per-project override.
- A route change sends in-flight submitted sheets back to step 1; the dialog warns with the count.

**Coordinator's calls:**
- SysAdmin creates custom roles.
- TemplateAdmin sets template routes.
- A custom role does NOT get the Approve grant automatically.
- LandApprover → Approver only on the human's word later, never by migration or seed.
- Approver gets the Approve grant level, not Manage.

**Design (coordinator 16.11):**
- No stored role sets.
- A step of a TEMPLATE-scoped route is satisfied by the step's own role OR the built-in general Approver role.
- One rule, used in both EditRules.CanApprove and the reopen-of-approved check (LastApprovalStepRoleAsync, the D2 place).
- Portion 1's contract also covers AA's checklist C-1…C-11 (`.sync-local\reports\B155-client-plan.md`): the role list for non-ManageRoles users, the affected-sheets count for the warning, and the project's template list.

- C-10 is dropped.
- C-8 (coordinator): the impact count = Submitted sheets only. It covers all projects the template route applies to, except projects with their own override, filtered by what the caller may see, plus a total. PUT returns the sheets that restarted.

**Order:**
- D1/D2 are current defects: `FindProjectRouteAsync` has no scope filter, and the reopen role uses the project's version instead of the document's own. They go in a small lane right after B150, red-first, F13.
- B155 code comes after B151, in two portions: migration + contract, then client.

Analysis: `.sync-local\reports\B155-approver-roles-analysis.md`.
