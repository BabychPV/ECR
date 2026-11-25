---
name: methodology-descriptor-code-is-version
description: "MethodologyDescriptor.Code holds the methodology VERSION label (\"1.0.0.0\"), not the methodology code — use MethodologyId"
metadata:
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-04T09:36:32.657Z
---

`MethodologyResolver` builds `MethodologyDescriptor(methodologyId, version.Id, version.Version, version.Version, …)` — its `Code` is the version label. AN-69 (04.11) stored it as "methodologyCode" and three old `NoMatchingRuleDiagnosticTests` caught it only because a positional record gained a field.

**Why:** the name lies; a reviewer reading `binding.Descriptor.Code` assumes the methodology code.
**How to apply:** for methodology identity take `Descriptor.MethodologyId` and resolve the code by join (`cfg.Methodology`) at read time. Adding a field to a positional record used in `Assert.Equal` changes equality for every existing assert — grep `new <Record>(` in tests first.
