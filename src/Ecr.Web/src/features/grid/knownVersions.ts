import type { TableSliceDto } from '@/api/types';
import { sameColumnValue } from './cellValue';
import { columnIndexOf, rowIndexOf } from './rowIndex';
import type { PendingEdit } from './useCellPatch';

/*
 * ⚠ Окремим модулем, а не в `edits.ts` (`D-132`): `autosave.ts` (статичний імпорт сторінки документа)
 * потребує лише цієї функції, а `edits.ts` тягне за собою буфер обміну, округлення й права комірок —
 * код сітки, що живе в її лінивому чанку.
 */

/**
 * Правки з ОСТАННЬОЮ відомою версією рядка — у мить надсилання, а не введення
 * (`B-09`).
 *
 * ⛔ Доти `baseVersion` запам'ятовувалась при введенні (`captureEdit`) і їхала
 * такою назавжди. Правка, яку сервер відхилив (`422`) чи яка чекала повтору,
 * лишалась зі СТАРОЮ версією, хоча наступний успішний патч того самого рядка
 * вже підняв її в кеші (`applyPatchLocally`). Повтор ішов зі старою версією і
 * діставав `409` на власних змінах — і так при кожному повторі, вічно.
 *
 * ⚠ «Остання відома» — це версія рядка, який людина БАЧИТЬ: кеш зрізу несе
 * тільки те, що сервер віддав цьому екрану (зріз або відповідь на власний
 * патч). Чужа правка, якої екран не бачив, версію в кеші не піднімає, тож
 * конфлікт із нею й далі ловиться — оптимістичне блокування не послаблене.
 *
 * @param overrides Версії, які людина обрала ЯВНО («Keep mine» у діалозі
 * конфлікту, `conflicts[].currentVersion`): вони сильніші за кеш — саме кеш у
 * цю мить і застарів.
 */
export function withKnownVersions(
  edits: readonly PendingEdit[],
  slice: TableSliceDto | undefined,
  overrides?: ReadonlyMap<string, string>,
): PendingEdit[] {
  const rows = slice === undefined ? undefined : rowIndexOf(slice);

  // ⛔ AN-39 / L8-20: рядки, де значення комірки в кеші вже НЕ те, що людина
  // бачила при введенні (`before`), - його змінив хтось інший, а екран
  // дізнався про це перезапитом зрізу (Recall/Reopen, фокус, L8-02). Нова
  // версія з кешу тут означала б тихий перезапис чужого значення: утримана
  // (відхилена) правка чекає довго, і повтор ішов би з версією, під якою
  // чуже значення вже лежить. Такий рядок лишається зі СВОЄЮ версією - сервер
  // відповість 409, і людина вирішить у діалозі конфлікту. Рядком, а не
  // коміркою: версія в запиті одна на рядок (`buildRequest`).
  const foreign = new Set<string>();
  if (rows !== undefined) {
    for (const edit of edits) {
      if (edit.before === undefined) continue;

      const row = rows.get(edit.rowKey);
      if (row !== undefined && !sameSavedValue(slice, edit.columnCode, row.cells[edit.columnCode] ?? null, edit.before)) {
        foreign.add(edit.rowKey);
      }
    }
  }

  return edits.map((edit) => {
    const override = overrides?.get(edit.rowKey);
    const known =
      override ?? (foreign.has(edit.rowKey) ? undefined : rows?.get(edit.rowKey)?.rowVersion);

    // ⚠ Рядка в кеші немає (зріз ще не завантажено, або він новий) — версія
    // лишається та, з якою правку зроблено: вигадувати іншу нема з чого.
    return known === undefined || known === edit.baseVersion ? edit : { ...edit, baseVersion: known };
  });
}

/**
 * Чи те саме значення в кеші, що людина бачила (`before`) - за типом колонки.
 *
 * ⛔ Рев'ю AN-39b P3-1: дата в кеші буває у двох формах - клієнта (`2026-09-15`, після
 * локального застосування патча) і сервера (`2026-09-15T00:00:00`, після перезапиту зрізу).
 * `sameCellValue` назвав би їх різними, і L8-20 прийняв би власне збережене за чуже -
 * `409` на власних змінах. Те саме правило, що й у `captureEdit`.
 */
function sameSavedValue(slice: TableSliceDto | undefined, columnCode: string, cached: unknown, before: unknown): boolean {
  const dataType = slice === undefined ? undefined : columnIndexOf(slice).get(columnCode)?.dataType;

  return sameColumnValue(dataType, cached, before);
}
