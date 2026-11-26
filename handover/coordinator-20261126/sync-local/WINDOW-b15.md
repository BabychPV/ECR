# Вікно b15 — аудит (lane/integ-audit-filters-live @ 6260abdb8), GO людини 26.11 «так»
- Репетиція на EcrLandB14Reh, той самий SHA: скрипт міграцій ідентичний b14 (sha256), idempotent ×2 = 0, setup -Upgrade rc 0 (125 с), Api :5202 health 200, журнал пишеться, лічильники 0 різниць; audit doc 96 764–1422 мс, doc 100 733–843 мс. PASS.
- 09:0x COPY_ONLY бекап E:\EcrBackup\land-ui\b15\bak\EcrLandUi-before-b15-*.bak (28 с, VERIFYONLY ok, sha256); лічильники до.
- 09:10:20 стоп b14 (PID 22168/10620); stop-live кинув «port 5094 still listening» (3 с замало — сокет звільнився за секунди; власника не було) → урок 566.
- idempotent ×2 = 0; setup -Upgrade rc 0 66 с; 09:11:51 старт app-b15 detached: API 43488, web 48244, ready 200; ProductVersion 6260abdb8. Простій ~2 хв. LIVE-SHA = 6260abdb8.
- Start-LiveDetached -ProcessId 43488 OK (ecr-20261126_001.log). Лічильники після = до (0).
- Live через API (тимч. f13-b15-sa, потім IsActive=0, файл видалено): audit/cells doc 96 — 200, 1402/728/703 мс; doc 100 — 200, 746/749/743 мс (до: тайм-аут 503). out\audit-live-timing.txt.
- UI-прохід — чекає входу людини.