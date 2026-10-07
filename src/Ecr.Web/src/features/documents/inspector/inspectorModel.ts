import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';

/**
 * Модель вкладки Issues інспектора документа (`UI-25`, макет
 * `docs/design/hybrid/screen-document.js` → `renderInspector`, KIT.md §4 `.aside`).
 *
 * ⛔ БЕЗПЕКА (P1, прихований аркуш). Група будується ЛИШЕ для таблиці, яку
 * сервер віддав у структурі документа (`GET …/tables`), тобто для таблиці
 * аркуша, який людина бачить. Зауваження з `tableDefId`, якого в структурі
 * немає, не показується і НЕ рахується: ні його текст, ні назва таблиці, ні
 * лічильник. Інакше сервер, що (ще) віддає зауваження всього документа, через
 * інспектор показав би число й текст проблем прихованого аркуша. Це другий
 * рубіж — перший на сервері (`lane/analiz/sec-docs-hidden-sheet-scope`).
 *
 * ⚠ Агрегати по аркушах тут не рахуються взагалі: лічильник — кількість
 * ПОКАЗАНИХ зауважень, а не сума по документу.
 */

/** Одне зауваження в групі. */
export interface InspectorIssue {
  readonly finding: ValidationFindingDto;
  /** Позиція в переліку сервера — стабільний ключ рендеру. */
  readonly index: number;
}

/** Група = одна таблиця (макет: `group-h` із назвою таблиці й кількістю). */
export interface InspectorIssueGroup {
  readonly tableDefId: number;
  readonly sheetCode: string;
  readonly title: string;
  readonly issues: readonly InspectorIssue[];
}

/** Підсумок для кнопки «K issues» і лічильника вкладки. */
export interface InspectorIssueCounts {
  readonly all: number;
  readonly errors: number;
  readonly warnings: number;
}

/** Назва таблиці для заголовка групи: номер (код) і локалізована назва. */
export function tableTitleOf(table: DocumentTableDto): string {
  const name = localized(table.tableNameL10n);

  return name.length > 0 ? `${table.tableCode} ${name}` : table.tableCode;
}

/**
 * Зауваження, згруповані за таблицями, у порядку таблиць документа (аркуш,
 * потім таблиця), — так само, як їх бачить людина, гортаючи сітки.
 *
 * ⚠ Таблиця з кількома екземплярами (динамічні копії) — одна група: адреса
 * зауваження — `tableDefId`, а не екземпляр.
 */
export function groupIssues(
  messages: readonly ValidationFindingDto[],
  tables: readonly DocumentTableDto[],
): InspectorIssueGroup[] {
  const visible = new Map<number, DocumentTableDto>();

  for (const table of tables) {
    if (!visible.has(table.tableDefId)) visible.set(table.tableDefId, table);
  }

  const byTable = new Map<number, InspectorIssue[]>();

  messages.forEach((finding, index) => {
    // ⛔ Таблиці немає серед видимих — зауваження не існує для цієї людини.
    if (!visible.has(finding.tableDefId)) return;

    const list = byTable.get(finding.tableDefId) ?? [];
    list.push({ finding, index });
    byTable.set(finding.tableDefId, list);
  });

  return [...byTable.entries()]
    .map(([tableDefId, issues]) => {
      const table = visible.get(tableDefId) as DocumentTableDto;

      return {
        tableDefId,
        sheetCode: table.sheetCode,
        title: tableTitleOf(table),
        // ⚠ Помилки перед попередженнями: перше, що треба виправити, — те, що
        // не дасть подати аркуш.
        issues: [...issues].sort(
          (a, b) => severityRank(a.finding.severity) - severityRank(b.finding.severity) || a.index - b.index,
        ),
        order: [table.sheetOrdinal, table.tableOrdinal] as const,
      };
    })
    .sort((a, b) => a.order[0] - b.order[0] || a.order[1] - b.order[1])
    .map(({ order: _order, ...group }) => group);
}

function severityRank(severity: string): number {
  return severity === 'Error' ? 0 : 1;
}

/** Лічильники лише за ПОКАЗАНИМИ групами (див. коментар модуля). */
export function countIssues(groups: readonly InspectorIssueGroup[]): InspectorIssueCounts {
  let errors = 0;
  let all = 0;

  for (const group of groups) {
    for (const issue of group.issues) {
      all += 1;
      if (issue.finding.severity === 'Error') errors += 1;
    }
  }

  return { all, errors, warnings: all - errors };
}

/**
 * Чип адреси зауваження (макет: `R4 · C3`).
 *
 * ⚠ Макет друкує ПОЗИЦІЇ рядка й колонки; сервер віддає КЛЮЧ рядка й КОД
 * колонки (`ValidationFindingDto`), і саме вони стоять у шаблоні й журналі.
 * Позицію клієнт міг би вирахувати лише зі зрізу таблиці, якого в інспекторі
 * немає (і не має бути: зріз живе в чанку сітки). Тому чип — ключі.
 * Зауваження до таблиці цілком (без рядка) — прочерк, а не порожнеча.
 */
export function addressChip(rowKey: string | null, columnCode: string | null): string {
  return `${rowKey ?? '—'} · ${columnCode ?? '—'}`;
}

/**
 * Лічильник таблиці зі статусу (`BE-10`): `null` — «не перевіряли» або
 * «приховано», і це НЕ нуль. Нуль на місці невідомого — неправда, на яку
 * спираються, подаючи звітність.
 */
export function countOrDash(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : String(value);
}
