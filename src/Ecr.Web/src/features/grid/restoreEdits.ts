import type { PendingEdit } from './useCellPatch';
import type { RestoreSlice } from './lostEdits';
import { putPendingEdit } from './pendingStore';
import { scheduleAutosave } from './autosave';

/**
 * Рішення про кожну збережену правку: повертати чи показати конфлікт
 * (`ФВ-3.6`, `ФВ-3.7`).
 *
 * ⛔ Правка їхала з `baseVersion` — версією рядка на момент, коли її вводили.
 * Поки сесія лежала, той самий рядок міг змінити хтось інший. Застосувати
 * правку мовчки означало б одне з двох: або `409` від сервера через дві
 * секунди після того, як людина натиснула «Відновити» (і тоді незрозуміло, за
 * що), або — якби ми підставили свіжу версію — ТИХЕ затирання чужого числа
 * звітності. Обидва варіанти гірші за «рядок змінився, правку пропущено».
 *
 * ⚠ Тому модуль чистий: жодного React, жодного `fetch`. Версії рядків йому
 * ПЕРЕДАЮТЬ — саме це й дозволяє перевірити правило конфлікту без сервера,
 * сітки й сторінки.
 */

/** Чому правку не застосовано. */
export type RestoreConflictReason =
  /** Версія рядка змінилася, доки сесія лежала. */
  | 'version'
  /** Зріз прочитати не вдалося — версії невідомі, а гадати ми не будемо. */
  | 'unavailable';

/** Правка, яку повертати не можна, і чому саме. */
export interface RestoreConflict {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly edit: PendingEdit;
  readonly reason: RestoreConflictReason;
  /** Версія рядка ЗАРАЗ; `null` — рядка в зрізі немає (або зріз недоступний). */
  readonly currentVersion: string | null;
}

/** Правка, яку повертаємо в сховище незбереженого. */
export interface RestoreApplied {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly edit: PendingEdit;
}

/** Що буде зроблено, якщо натиснути «Застосувати». */
export interface RestorePlan {
  readonly applied: readonly RestoreApplied[];
  readonly conflicts: readonly RestoreConflict[];
}

/**
 * Версії рядків зрізу: `rowKey` → `rowVersion`; `null` — зріз недоступний.
 *
 * ⛔ `null` і порожня мапа — РІЗНІ відповіді. Порожня мапа означає «зріз
 * прочитано, рядків у ньому немає», тобто правка створення рядка
 * (`baseVersion: null`, `R-B2`) законно застосовується. `null` означає «не
 * знаємо», і приймати його за порожній зріз — це вигадати факт про дані.
 */
export type SliceVersions = ReadonlyMap<string, string> | null;

/**
 * Розподіляє збережені правки на «повернути» і «конфлікт».
 *
 * ⚠ Правило одне й симетричне: правка повертається тоді й лише тоді, коли
 * версія рядка ЗАРАЗ дорівнює тій, з якою правку вводили. Звідси випливає
 * решта випадків без окремих гілок:
 *   — рядок на місці й не змінювався (`'AAA=' === 'AAA='`) → повертаємо;
 *   — рядок змінив хтось інший (`'BBB=' !== 'AAA='`) → конфлікт;
 *   — правка створювала рядок (`null`), і рядка досі немає → повертаємо;
 *   — правка створювала рядок, а він уже існує (хтось додав) → конфлікт;
 *   — рядок був, а тепер його немає (`null !== 'AAA='`) → конфлікт.
 */
export function planRestore(
  slices: readonly RestoreSlice[],
  versionsOf: (tableInstanceId: number, periodKey: number) => SliceVersions,
): RestorePlan {
  const applied: RestoreApplied[] = [];
  const conflicts: RestoreConflict[] = [];

  for (const slice of slices) {
    const versions = versionsOf(slice.tableInstanceId, slice.periodKey);

    for (const edit of slice.edits) {
      const address = { tableInstanceId: slice.tableInstanceId, periodKey: slice.periodKey };

      if (versions === null) {
        conflicts.push({ ...address, edit, reason: 'unavailable', currentVersion: null });
        continue;
      }

      const currentVersion = versions.get(edit.rowKey) ?? null;

      if (currentVersion === edit.baseVersion) {
        applied.push({ ...address, edit });
        continue;
      }

      conflicts.push({ ...address, edit, reason: 'version', currentVersion });
    }
  }

  return { applied, conflicts };
}

/**
 * Кладе відібрані правки назад у сховище незбереженого.
 *
 * ⚠ Саме в `pendingStore`, а не прямим `PATCH`: відновлена правка — це
 * звичайна незбережена зміна, і далі з нею працює той самий автозбереження
 * (`scheduleAutosave`), той самий сторож виходу (`unsavedSources`), той самий
 * показ відмов. Окремий шлях надсилання довелося б тримати в синхроні з
 * головним, а розійшлися б вони мовчки.
 *
 * @returns Скільки правок повернуто.
 */
export function applyRestorePlan(plan: RestorePlan): number {
  for (const item of plan.applied) {
    putPendingEdit(item.tableInstanceId, item.periodKey, item.edit);
  }

  if (plan.applied.length > 0) scheduleAutosave();

  return plan.applied.length;
}
