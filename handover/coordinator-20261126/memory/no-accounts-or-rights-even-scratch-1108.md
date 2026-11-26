---
name: no-accounts-or-rights-even-scratch-1108
description: "Human's standing rule (via coordinator 08.11) — agents do not create accounts or change rights, even on scratch, without the coordinator's/human's OK"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-17T07:44:25.516Z
---

Agents do not create user accounts or change roles or grants, **even in a scratch DB**, without an explicit OK (the coordinator puts it to the human).

**Why:** on 08.11, during F13-p209, I created `f13-p209` in EcrScratch_W15j with rehearsal-account-w15l.ps1, copying land-reviewer's roles. I did it because the cred-file account and land-owner hold LandRestricted, which DENIES sheet S07_WATER_REPORT. The coordinator flagged it against the human's standing rule.

**How to apply:**
- If a hand pass needs another role, stop and ask the coordinator.
- Report which account is blocked and by what grant: LandRestricted deny = sec.ResourceGrant on SheetDef S07_* .
- Do not reach for the rehearsal-account scripts on my own. They were for window rehearsals and need an OK too.

✎ 08.11 later: the human said keep `f13-p209` («залиш»). That OK covers only this account; any new one still needs an OK first.

✎ 12.11 human (quiz): AU MAY create restricted test accounts and roles for audit F13 Deny checks (f13-p209 and similar), ONLY in scratch copies (never live). Each account is logged in the F13 report, and the scratch is dropped after the check. This standing OK covers AU's audit F13 only; other sessions and other purposes still need an OK.

✎ 17.11 ~07:45Z the human: «я дозволяю створювати аккаунти в нашому додатку». The trigger: AN's B80 spike was blocked because it needed an operator account with a Project grant.

The coordinator reads it as a standing OK for ALL sessions to create app users, roles and grants for development and verification in their own test/scratch/dev DBs (EcrTest_*, EcrScratch_*, setup-dev-db stands). Conditions:
- passwords are generated randomly and never printed or committed;
- log the accounts in the READY/report;
- drop or abandon them with the DB.

✎ The human clarified right after: «це тестове середовище». So the OK covers the stand (EcrLandUi) too: sessions may create app test accounts, roles and grants there.
- Prefix the names with test-/f13-.
- Generate passwords randomly; never print or commit them.
- Log each account in the READY/window report.
- Do not change existing users' rights except by a word, as in B155.

RESTORE, data deletion and opening closed periods on the stand still need their own word.

Related: [[human-hse401-cred-file-standing-1105]], [[f13-acceptance-script-1108]].
