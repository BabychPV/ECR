# Картка: Sources «New connection» — підказки і 3 дефекти
Статус: main
Примітка: 24.11 код у main і на live b3060f0fd (SHA AC інші - коміти влиті під іншими хешами), перевірки на live ще немає.
Гілка: lane/integ-source-hints
Пачка:
Ризик: R2
Рішення: людина 23.11 «додай підсказки тут щоб розуміти що треба заповнити з прикладом»
Дизайн: не потрібен

## 1. Що і навіщо
- Дослівно: «додай підсказки тут щоб розуміти що треба заповнити з прикладом».
- Хто побачить: Configure → Sources → New connection.

## 2. Критерії приймання
| AC | Що спостерігаємо | Доказ | SHA |
|----|------------------|-------|-----|
| AC-0 | live: New connection — під кожним полем пояснення і приклад; зміна Transport перемикає тексти; Sql ховає Secondary endpoint | live: скриншоти Sql / PI SQL / PI Web API | ab8a03bb3 |
| AC-1 | підказки й приклади за транспортом (16 ключів en/ru/kz) | vitest hints 3/3 | e2a82ea6e |
| AC-2 | MaxParallel діє під час збору (PiWebApi), мутація червона, 20/20 | CollectionMaxParallelTests | 132fe498f |
| AC-3 | для Sql/PiSqlClient адреса лише як рядок з'єднання (422) | DataSourceTransportFieldsTests | b18904e89 |
| AC-4 | поля, яких транспорт не читає, приховані; сервер відмовляє (422); кнопка «Clear» у шкалі розмірів | vitest + тести сервера + buttonScale | ce194e1ac |
| AC-5 | e2e діалогу | sourceFormHints.spec.ts (CI) | ab8a03bb3 |
| AC-R | SMTP-адреса без змін (CheckSqlServerAddress не чіпали) | тести політики | b18904e89 |
