import { notifications } from '@mantine/notifications';
import type { TableSliceDto } from '@/api/types';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { notificationCloseButtonProps } from '@/shared/ui/notify';
import type { PendingEdit } from './cellPatchRequest';
import { withKnownVersions } from './knownVersions';

/*
 * ⛔ PSS-17 (рішення людини №12, 20.11): пакет правок, більший за стелю сервера, ДІЛИТЬСЯ на частини, а не
 * відхиляється. Доти вставка (заливка, Undo великої вставки, накопичене автозбереження) їхала ОДНИМ `PATCH`, і
 * сервер відповідав `422 ECR-REQ-0422 patchTooLarge` на весь пакет — без жодного шляху зберегти таке взагалі.
 *
 * ⚠ Окремим модулем, а не в `DocumentGrid.tsx`: ділять ОБИДВА зберігачі зрізу — сітка (`save`) і документ за
 * розмонтовану сітку (`autosave.ts`, `saveOrphanSlice`); розійдись вони, і «понад стелю» зберігалося б лише з
 * екрана. Модуль — статичний граф сторінки документа (як `autosave.ts`), тож тягне лише дешеве.
 *
 * ⚠ Що НЕ тут. Перевірка всієї вставки — до першої частини, як і раніше, і не цим модулем: `planPaste` (права,
 * «рахує система», неоднозначні числа — весь буфер або нічого), діалоги причини ручного значення й
 * підтвердження (один на пакет), `computedEditsOf` у зберігачі (обчислювані комірки — до поділу). Сюди
 * доходить уже перевірений пакет; частини лише везуть його.
 */

/**
 * Стеля комірок на один `PATCH …/cells` — дзеркало `PatchCellsRequest.MaxCells` (`WR-11`).
 *
 * ⚠ У контракт (OpenAPI) стеля не винесена — лише в текст відмови (`err.ECR-REQ-0422.patchTooLarge`, `max`).
 * Тому — одна константа тут, і саме її бере і поділ, і сторож у тестах (`patchParts.test.ts`). Сервер
 * зменшить стелю — частини почнуть діставати `422`, і тест-сторож на стороні сервера (`CellWriteRoundTripTests`)
 * про це не скаже: міняти разом.
 */
export const PatchCellsMaxCells = 50_000;

/**
 * Ділить правки зрізу на частини ≤ `max` комірок.
 *
 * ⚠ Рядок не розривається, доки вміщується: версія рядка (`baseVersion`) одна на рядок запиту, і рядок, розірваний
 * між частинами, вимагав би версію з відповіді попередньої частини. Лише рядок, БІЛЬШИЙ за стелю (теоретичний
 * випадок — колонок десятки), ріжеться — його наступна частина бере версію з кешу (`saveInParts`).
 *
 * ⚠ Пакет у межах стелі повертається ОДНІЄЮ частиною і тим самим масивом — запит не відрізняється від того, що
 * йшов до поділу.
 */
export function splitIntoParts(edits: readonly PendingEdit[], max: number = PatchCellsMaxCells): PendingEdit[][] {
  if (edits.length <= max) return edits.length === 0 ? [] : [[...edits]];

  const rows = new Map<string, PendingEdit[]>();
  for (const edit of edits) {
    const row = rows.get(edit.rowKey);
    if (row === undefined) rows.set(edit.rowKey, [edit]);
    else row.push(edit);
  }

  const parts: PendingEdit[][] = [];
  let current: PendingEdit[] = [];

  for (const row of rows.values()) {
    if (current.length + row.length > max && current.length > 0 && row.length <= max) {
      parts.push(current);
      current = [];
    }

    // Рядок, більший за стелю, — порціями: заповнюємо поточну частину до стелі й переходимо на наступну.
    let rest = row;
    while (current.length + rest.length > max) {
      const room = max - current.length;
      current.push(...rest.slice(0, room));
      rest = rest.slice(room);
      parts.push(current);
      current = [];
    }
    current.push(...rest);
  }

  if (current.length > 0) parts.push(current);

  return parts;
}

/** Підсумок збереження частинами. */
export interface PartsOutcome {
  /** Комірок у пакеті. */
  readonly total: number;
  /** Комірок, які сервер ПРИЙНЯВ (сума успішних частин). */
  readonly saved: number;
  /** Частин у пакеті. */
  readonly parts: number;
  /** Номер (з нуля) частини, на якій зупинилися; `null` — пройшли всі. */
  readonly failedPart: number | null;
}

interface SaveInPartsOptions {
  /**
   * Кеш зрізу в мить надсилання ЧАСТИНИ: відповідь попередньої частини вже підняла в ньому версії рядків
   * (`applyPatchLocally`), і наступна бере їх звідти (`B-09`, `withKnownVersions`).
   */
  readonly slice: () => TableSliceDto | undefined;
  /** Версії, які людина обрала явно («Keep mine»), — як у зберігача. */
  readonly versionOverrides?: ReadonlyMap<string, string> | undefined;
  /** Стеля частини (тести); за замовчуванням — `PatchCellsMaxCells`. */
  readonly max?: number;
}

/**
 * Надсилає пакет частинами ПОСЛІДОВНО: наступна — лише після відповіді попередньої.
 *
 * ⛔ Не паралельно: кожна частина — окрема транзакція запису на сервері (до ~6 с на 50 000 комірок), і пачка
 * одночасних таких транзакцій — рівно та ціна, від якої стеля сервера захищає.
 *
 * ⛔ Перша відмова ЗУПИНЯЄ надсилання. Прийняте раніше лишається прийнятим (частина — окремий атомарний запит;
 * відкотити її означало б іще один запис поверх, і він міг би впасти так само), а решта — НЕ губиться: усі правки
 * пакета вже лежать у сховищі (`pendingStore`), успіх частини знімає зі сховища лише її правки
 * (`discardPendingRows` у `send`), тож ненадіслане лишається на екрані незбереженим, із тим самим шляхом
 * довезення, що й у будь-якої відмови (`V-01`: «Retry save», діалог конфлікту, автозбереження). Скільки
 * збережено і скільки ні — людині кажуть прямо (`reportParts`).
 *
 * ⚠ Історія Undo від цього не розходиться з екраном: крок вставки один на весь пакет, а екран показує ВСІ
 * вставлені значення — збережені й незбережені. Ctrl+Z повертає обидві групи (збережене — новим записом,
 * незбережене — заміною в сховищі), тобто рівно те, що людина бачить.
 *
 * @param send Надсилає ОДНУ частину і повертає, чи прийняв її сервер (відмову показує сам — банер сітки чи тост).
 */
export async function saveInParts(
  edits: readonly PendingEdit[],
  send: (part: PendingEdit[]) => Promise<boolean>,
  options: SaveInPartsOptions,
): Promise<PartsOutcome> {
  const parts = splitIntoParts(edits, options.max);
  const total = edits.length;
  const report = parts.length > 1 ? reportParts(total, parts.length) : null;

  // Рядки, уже надіслані попередніми частинами: «Keep mine» для них застарів — версію підняла відповідь.
  const sentRows = new Set<string>();
  let saved = 0;

  for (const [index, raw] of parts.entries()) {
    const part = index === 0 ? raw : withKnownVersions(raw, options.slice(), withoutRows(options.versionOverrides, sentRows));
    for (const edit of part) sentRows.add(edit.rowKey);

    if (!(await send(part))) {
      const outcome = { total, saved, parts: parts.length, failedPart: index };
      report?.stopped(outcome);

      return outcome;
    }

    saved += part.length;
    report?.progress(saved);
  }

  const outcome = { total, saved, parts: parts.length, failedPart: null };
  report?.finished(outcome);

  return outcome;
}

function withoutRows(
  overrides: ReadonlyMap<string, string> | undefined,
  rows: ReadonlySet<string>,
): ReadonlyMap<string, string> | undefined {
  if (overrides === undefined || rows.size === 0) return overrides;

  return new Map([...overrides].filter(([rowKey]) => !rows.has(rowKey)));
}

/** Номер тоста: два пакети частинами (дві сітки) не мають оновлювати один тост. */
let reportSequence = 0;

/**
 * Видимий хід збереження частинами — лише для пакета з кількох частин.
 *
 * ⚠ Тост, а не банер сітки: частинами зберігає й документ за розмонтовану сітку (`saveOrphanSlice`), де банера
 * немає. Причину відмови показує зберігач, як і для одного запиту; тут — лише лік: скільки прийнято, скільки ні.
 */
function reportParts(total: number, parts: number): {
  progress(saved: number): void;
  stopped(outcome: PartsOutcome): void;
  finished(outcome: PartsOutcome): void;
} {
  reportSequence += 1;
  const id = `ecr-patch-parts-${String(reportSequence)}`;
  const progressText = (saved: number): string =>
    t('grid.saveParts.progress', { saved: formatNumber(saved), total: formatNumber(total) });

  notifications.show({
    id,
    loading: true,
    autoClose: false,
    withCloseButton: false,
    message: progressText(0),
  });

  return {
    progress(saved) {
      notifications.update({ id, loading: true, autoClose: false, withCloseButton: false, message: progressText(saved) });
    },
    stopped(outcome) {
      // Перша ж частина не пройшла — не збережено нічого, і причину вже показує зберігач: лік «0 з N» нічого не
      // додає до неї.
      if (outcome.saved === 0) {
        notifications.hide(id);
        return;
      }

      notifications.update({
        id,
        loading: false,
        color: 'statusWarning',
        autoClose: false,
        withCloseButton: true,
        closeButtonProps: notificationCloseButtonProps,
        message: t('grid.saveParts.partial', {
          saved: formatNumber(outcome.saved),
          total: formatNumber(outcome.total),
          rest: formatNumber(outcome.total - outcome.saved),
        }),
      });
    },
    finished(outcome) {
      notifications.update({
        id,
        loading: false,
        color: 'statusSuccess',
        autoClose: 4000,
        withCloseButton: true,
        closeButtonProps: notificationCloseButtonProps,
        message: t('grid.saveParts.done', { total: formatNumber(outcome.total), parts: formatNumber(parts) }),
      });
    },
  };
}
