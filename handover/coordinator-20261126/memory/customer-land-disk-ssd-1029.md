---
name: customer-land-disk-ssd-1029
description: "Людина 29.10: дані Land у замовника на SSD; наш стенд EcrLandUi на HDD F: — cold-заміри на ньому не цифри продукту"
metadata:
  node_type: memory
  type: project
  originSessionId: 7435a874-e4e3-49fa-9b54-7bfe3eb298ad
  modified: 2026-10-29T08:53:14.200Z
---

Людина 29.10: дані Land у замовника лежать на **SSD**. На цій машині E:/F: = HDD 5400 об/хв (Toshiba MQ04), H:/C: = NVMe SSD.

**Why:** T3_00 cold 25–35 с на HDD проти 4,1–4,5 с на NVMe (T3_00-COLD-SSD.md) — повільність холодного читання апаратна, не архітектурна; columnstore не допоміг.

**How to apply:** бюджет cold 15 с вважати виконаним для продукту; warm-up job і «виняток» не потрібні. Після W14n H: тримає живу EcrLandUi (11,8 ГБ вільно) — scratch/F13 БД лише на E:; SSD-перф-замір — лише після звільнення місця за моїм словом. Людина 29.10 сказала «так перенеси» EcrLandUi на SSD (вікно W14n, OPS-MOVE-EcrLandUi-TO-SSD.md). W14n виконано 29.10 ~10:13 (EcrLandUi на H:\EcrData\EcrLandUi_ssd, журнал 8,2 ГБ, контрольні суми рівні). Людина 29.10: старі файли F:\EcrData\EcrLandUi_* видалити «через кілька днів стабільної роботи — так» → не раніше ~01.11, після перевірки стабільності. Лишається ризик: нестабільність плану star-в'ю після оновлення статистики → lane plan-stability (Аналіз).
