# Картка: approver-edit-flake ApproverEditHttpFlowTests — чекати власного перерахунку перед поданням/погодженням
Статус: in-progress
Гілка: lane/integ-approver-edit-flake
Пачка: b12
Ризик: R1
Рішення: правило «без боргів» (людина 24.11); гейт OwnRecalculation b11 — поведінка продукту правильна
Дизайн: не потрібен

## 1. Що і навіщо
- Флейк з main: main ecdd84621 18/20, calc-orphans 12/20; подання/погодження одразу після PATCH, поки йде власний перерахунок документа → ECR-SUB-4221.
- Робоча папка: `F:\ecr-wt\approver-edit-flake`; база: `origin/main` @ `ecdd84621`
- Можна змінювати: tests/Ecr.Api.Tests (DocumentRecalculationWait.cs, ApproverEditHttpFlowTests, RejectedSheetHttpFlowTests, FormulaManualOverrideHttpFlowTests)
- Лише читати: продуктовий код (не змінюється)

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-1 | тести чекають ISourceCascadePendingGate=None перед submit/approve, без sleep і повторів на 422 | 20/20 на lane і на calc-orphans+фікс | |
| AC-R | гейт OwnRecalculation у продукті без змін | diff лише tests/ | |
