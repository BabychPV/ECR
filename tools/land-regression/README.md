# land-regression

Регресія розрахунку Land проти еталонних CSV з прода: інструмент заводить
документ через API ECR, записує вхідні значення, запускає перерахунок,
читає `calculation-results` і порівнює з очікуваними викидами
(відносний допуск за замовчуванням `1e-6`).

⛔ **Репозиторій публічний. Еталонні CSV, мапінг з реальними кодами та файли
результатів у git не комітяться** (`.gitignore` це перекриває). Числа в тестах
— вигадані макетні.

## Запуск

Потрібен Node ≥ 22.18 (ts виконується без збірки), залежностей немає.

```powershell
$env:ECR_USER = "…"; $env:ECR_PASSWORD = "…"
node src/run.ts --base-url http://localhost:5080 `
  --csv D:\local\land.csv --mapping D:\local\land.mapping.local.json `
  --out D:\local\land-result.json
```

Змінні замість аргументів: `ECR_BASE_URL`, `LAND_CSV`, `LAND_MAPPING`,
`LAND_OUT`. Код виходу: `0` — розбіжностей немає, `1` — є, виняток — збій.

## Мапінг

Приклад — `mapping.example.json`. Поля: `projectId`, `sheetDefId`,
`tableCode` (таблиця Land у документі), `periodKey`, `rowKeyColumn`
(ключ рядка; без нього `r1`, `r2`…), `inputs` (колонка CSV → код ColumnDef),
`expected` (колонка CSV → `OutputCode` у результатах), `tolerance`,
`reportUnexpected`.

CSV: роздільник `;` або `,`, десяткова кома й пробіли тисяч допускаються,
порожня вхідна клітинка пропускається, порожній еталон — помилка.

## Припущення

- Входи пишуться в динамічну таблицю: рядки створюються `POST …/rows`, комірки —
  `PATCH …/cells` з `origin: UserEdit`.
- Результат зіставляється за парою (`sourceRowKey` = ключ рядка, `outputCode`).
  Якщо методологія віддає `sourceRowKey` інакше — це видно як `missing`.
- Стани задачі: `Succeeded` — успіх; `Failed`/`Cancelled` — збій.

## Тести

```
npm test    # node --test, API замокано
```
