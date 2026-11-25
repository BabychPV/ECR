---
name: handout-waits-for-permits
description: 18.10 людина — видача ecr-msi чекає розділ Permits; проходи тестувальника на кінцевому SHA після Permits
metadata:
  type: project
---

18.10 16:29Z людина дослівно: «так Permits теж входять», далі «чекаємо Permits» — видача ecr-msi відкладена до готовності Permits (мокап погоджено, lane/permits/*, сесія Permites, префікс PM-nn, міграція одна).

**Why:** Permits — частина обсягу релізу. **How to apply:** проходи A/B на 9d1f3fb65 лишаються проміжним доказом; два чисті проходи (P0–P2=0) — на ФІНАЛЬНОМУ SHA після Permits; rc з UI довідників і Y5 мержити раніше, не чекаючи Permits; прод-нотатки (R2 VM, прегроу БД тощо) — перед видачею. Пов'язане: [[stable-before-handout]], [[human-quiz-answers-1015]].
