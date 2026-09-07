import type {
  SaveTableRelationRequest,
  TableRelationDto,
  TableRelationKind,
  TemplateStructureDto,
} from '@/api/types';

/**
 * Чернетка зв'язку між таблицями в редакторі (`ФВ-2.12`, `ФВ-2.13`).
 *
 * ⚠ Окремий тип від `TableRelationDto`. У DTO є `id`, `isEditable` і коди
 * таблиць — усе три рахує сервер, і жодного з них форма не задає. Правити
 * прямо DTO означало б, що форма тримає поля, які нікуди не надсилаються, і
 * рано чи пізно хтось спробує змінити в ній `isEditable`.
 */
export interface RelationDraft {
  /** Код зв'язку; після створення не змінюється — це його адреса в API. */
  readonly code: string;
  readonly sourceTableDefId: number | null;
  readonly targetTableDefId: number | null;
  readonly relationKind: TableRelationKind;
  readonly matchJson: string;
  readonly mapJson: string;
  readonly onSourceChange: number;
  readonly isActive: boolean;

  /** Чи це нова чернетка: код нової ще можна набрати. */
  readonly isNew: boolean;
}

/**
 * Види зв'язку — рівно ті, що є в переліку домену.
 *
 * ⛔ Перелік виписаний, а не зібраний із рядка: підпис `t(\`tables.kind.${kind}\`)`
 * невидимий для сторожа «кожен рядок, якого просить клієнт, є в каталозі», і
 * новий вид зв'язку мовчки з'явився б на екрані як `⟦tables.kind.…⟧`
 * (`D2-172`).
 */
export const RelationKinds: readonly TableRelationKind[] = [
  'Mirror',
  'Rollup',
  'Reference',
  'Cascade',
  'Check',
  'Copy',
];

/** Реакція на зміну джерела: 0 Recalc, 1 Warn, 2 Block. */
export const OnSourceChangeValues: readonly number[] = [0, 1, 2];

/** Таблиця версії у списку вибору. */
export interface TableOption {
  readonly id: number;
  readonly label: string;
}

/**
 * Таблиці версії для вибору джерела й приймача.
 *
 * ⛔ Береться зі **структури** версії, а не з окремого маршруту: другий
 * перелік тих самих таблиць розійшовся б із першим на першій же зміні
 * структури, і форма пропонувала б таблицю, якої у версії вже немає.
 *
 * ⚠ Підпис несе код аркуша: коди таблиць унікальні лише в межах аркуша, і
 * два `Main` у списку без цього не розрізнити.
 */
export function tableOptions(structure: TemplateStructureDto | undefined): TableOption[] {
  if (structure === undefined) return [];

  return structure.sheets.flatMap((sheet) =>
    sheet.tables.map((table) => ({ id: table.id, label: `${sheet.code} · ${table.code}` })),
  );
}

/** Порожня чернетка нового зв'язку. */
export function emptyDraft(): RelationDraft {
  return {
    code: '',
    sourceTableDefId: null,
    targetTableDefId: null,
    relationKind: 'Rollup',
    matchJson: '',
    mapJson: '',
    onSourceChange: 0,
    isActive: true,
    isNew: true,
  };
}

/** Чернетка з наявного зв'язку — для правки. */
export function draftOf(relation: TableRelationDto): RelationDraft {
  return {
    code: relation.code,
    sourceTableDefId: relation.sourceTableDefId,
    targetTableDefId: relation.targetTableDefId,
    relationKind: relation.relationKind,
    matchJson: relation.matchJson,
    mapJson: relation.mapJson ?? '',
    onSourceChange: relation.onSourceChange,
    isActive: relation.isActive,
    isNew: false,
  };
}

/** Що саме заважає зберегти чернетку. */
export type RelationBlocker = 'Code' | 'Source' | 'Target' | 'Self' | 'Match';

/**
 * Чому чернетку ще не можна зберегти; `null` — можна.
 *
 * ⛔ Це **не** копія серверних правил, а те саме питання, поставлене раніше.
 * Кожну з цих відмов сервер дає й сам (`ECR-CFG-0422` на код, `ECR-TMPL-0422`
 * на решту), і саме він лишається межею: форма, яка була б єдиною перевіркою,
 * впала б від першого прямого запиту. Тут вона існує рівно для того, щоб не
 * везти в мережу те, що напевно повернеться відмовою.
 *
 * ⛔ Повертається ПЕРЕЛІК, а не ключ рядка. Ключ звідси потрапив би в `t()`
 * змінною, і сторож «кожен рядок, якого просить клієнт, є в каталозі» його не
 * побачив би — забутий у seed підпис вийшов би на екран як `⟦tables.…⟧`
 * (`D2-172`).
 */
export function whyCannotSave(draft: RelationDraft): RelationBlocker | null {
  if (draft.code.trim().length === 0) return 'Code';
  if (draft.sourceTableDefId === null) return 'Source';
  if (draft.targetTableDefId === null) return 'Target';

  // ⛔ Та сама перевірка, що й `CK_Rel_NotSelf` у схемі. Без неї користувач
  // діставав би не відмову форми, а помилку бази.
  if (draft.sourceTableDefId === draft.targetTableDefId) return 'Self';

  // ⚠ Зв'язок без зіставлення виглядає налаштованим і не з'єднує жодного
  // рядка — саме та мовчазна порожнеча, яку ловить домен.
  if (draft.matchJson.trim().length === 0) return 'Match';

  return null;
}

/**
 * Тіло запиту `PUT …/relations/{code}`.
 *
 * ⚠ Порожній `mapJson` їде як `null`, а не як `''`: «перенесення немає» і
 * «перенесення описане порожнім рядком» — різні стани, і другий не має сенсу.
 */
export function relationBody(draft: RelationDraft): SaveTableRelationRequest {
  return {
    sourceTableDefId: draft.sourceTableDefId ?? 0,
    targetTableDefId: draft.targetTableDefId ?? 0,
    relationKind: draft.relationKind,
    matchJson: draft.matchJson.trim(),
    mapJson: draft.mapJson.trim().length === 0 ? null : draft.mapJson.trim(),
    onSourceChange: draft.onSourceChange,
    isActive: draft.isActive,
  };
}
