---
name: human-flert-owner-1111
description: "11.11 human: FLERT owns gas composition (ECR read-only daily copy); prod access via a read-only SQL login from the customer DBA"
metadata:
  node_type: memory
  type: project
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-11T21:09:43.589Z
---

Human quiz 11.11: (1) after go-live FLERT owns the stream gas composition; ECR keeps a read-only daily copy plus a manual sync button. This amends D-50/D-199 for gas composition only. (2) In prod, ECR reads FLERT directly through a separate read-only SQL login issued by the customer DBA (no linked server, D-268).

My decisions on the same plan (.sync-local\reports\FLERT-plan-1111.md): use usp_GetAllStreamDataFlattened; sum 0 or missing → issue; short component names win, with a warning; auto-recalc only open periods, one job per year; legacy season rule; scope is sour-gas flaring + Thermaloxidizer. AN implements F0–F8 after AN-164.

**How to apply:** don't re-ask; record the D-nn and the GRANTs in 11-install-guide. Related: [[human-hse400-template-1111]].
