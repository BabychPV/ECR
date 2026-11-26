# Картка: rm-snapshots-old стара гілка P-1124-3 (замінена на -2)
Статус: live-verified
Гілка: lane/integ-rm-report-snapshots
Пачка: lane/integ-b12
Ризик: R1
Рішення: P-1124-3
Дизайн: не потрібен

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-1 | усі коміти цієї гілки є в lane/integ-rm-report-snapshots-2 (cherry-pick) | git cherry | b2dccf328 |

Примітка: 24.11 усі коміти перенесено cherry-pick у lane/integ-rm-report-snapshots-2 (картка integ-rm-report-snapshots.md). Цю гілку НЕ зливати; прибрати після вікна b12 разом з робочою копією H:\ECR-wt\rm-snapshots.

Координатор 25.11: AC-1 на live під іншим SHA b2dccf328 (cherry-pick у b12, той самий заголовок і вміст) — прийнято.
