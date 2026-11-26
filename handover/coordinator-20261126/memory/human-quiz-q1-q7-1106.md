---
name: human-quiz-q1-q7-1106
description: Human 06.11 answered Q-1..Q-7 (HU-3) — flare _Day copy, frequency in UI, thousands EF, Web API off, Kestrel TLS, MSI A+B then C, licenses unconfirmed
metadata:
  type: project
---

Quiz 06.11 (HU-3), the human's own answers:
- Q-1: a) _Day is a copy; take only root _Auto/_Manual.
- Q-2: «це має бути параметр налаштування в UI»: the flare EF collection frequency is a UI setting (default hourly).
- Q-3: «Тисячі» EF per month; limits and health must scale.
- Q-4: keep PI Web API, disabled (AllowedHosts empty).
- Q-5: the app terminates TLS (Kestrel + SAN cert).
- Q-6: MSI signing A+B now (self-signed + SHA256), then C (customer CA).
- Q-7: licenses not legally confirmed; keep the default plus the caveat.

**Why:** these were open P1 questions blocking AN-4, CL-2 and CL-3.
**How to apply:** don't ask again. Audit-2 records them as D-records; Analiz implements Q-2/Q-3.
