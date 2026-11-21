import { createElement } from 'react';
import { notifications } from '@mantine/notifications';
import type { TableSliceDto } from '@/api/types';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { notificationCloseButtonProps } from '@/shared/ui/notify';
import type { PendingEdit } from './cellPatchRequest';
import { withKnownVersions } from './knownVersions';
import { cellKey, currentDocumentId, pendingSlice, subscribePending, wasSent } from './pendingStore';

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
 * Тому — одна константа тут, і саме її бере і поділ, і сторож у тестах (`patchParts.test.ts`, читає `.cs`).
 */
export const PatchCellsMaxCells = 50_000;

/**
 * Ділить правки зрізу на частини ≤ `max` комірок.
 *
 * ⚠ Рядок не розривається, доки вміщується: версія рядка (`baseVersion`) одна на рядок запиту, і рядок, розірваний
 * між частинами, вимагав би версію з відповіді попередньої частини. Лише рядок, БІЛЬШИЙ за стелю (теоретичний
 * випадок — колонок десятки), ріжеться — його наступна частина бере версію з кешу (`saveInParts`).
 *
 * ⚠ Такий рядок, якщо він НОВИЙ (`baseVersion: null` — створення, R-B2), у другій частині теж поїде з `null`, якщо
 * відповідь першої ще не поклала його в кеш зрізу (`withKnownVersions` лишає версію правки, коли рядка в кеші
 * немає). Сервер тоді відповість конфліктом на «створення» вже створеного рядка — і це чесна зупинка, а не тихий
 * запис: решта лишається незбереженою з Retry. За ~60 колонок на рядок до цього випадку — три порядки.
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

/**
 * Що сталося з однією частиною.
 *
 * - `saved` — сервер прийняв;
 * - `refused` — відмова (причину вже показав зберігач: банер сітки чи тост);
 * - `waiting` — «дані зайняті», повтор уже заплановано (`scheduleBusyRetry`): помилки людина не бачить, лише стан
 *   «чекає» — і текст про решту мусить казати саме це, а не «див. помилку».
 */
export type PartResult = 'saved' | 'refused' | 'waiting';

/** Підсумок збереження частинами. */
export interface PartsOutcome {
  /** Комірок у пакеті. */
  readonly total: number;
  /** Комірок, які сервер ПРИЙНЯВ (сума успішних частин). */
  readonly saved: number;
  /**
   * Комірок, які НЕ поїхали, бо людина вже змінила чи скасувала їх, доки йшли попередні частини: їхнє нове значення
   * повезе звичайне автозбереження, щойно звільняться рядки.
   */
  readonly superseded: number;
  /** Частин у пакеті. */
  readonly parts: number;
  /** Номер (з нуля) частини, на якій зупинилися; `null` — пройшли всі. */
  readonly failedPart: number | null;
  /** Чому зупинилися; `null` — не зупинялися. `left` — документ закрито посеред пакета. */
  readonly stoppedBy: 'refused' | 'waiting' | 'left' | null;
}

interface SaveInPartsOptions {
  /** Зріз — для звірки ненадісланих частин зі сховищем і для одного тосту на зріз. */
  readonly tableInstanceId: number;
  readonly periodKey: number;
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

/** Скільки пакетів частинами зараз іде (для `flushAutosaveAndSettle`: вихід чекає, доки пакет іде). */
let running = 0;

/** Чи йде зараз бодай один пакет частинами. */
export function isSavingInParts(): boolean {
  return running > 0;
}

/**
 * Надсилає пакет частинами ПОСЛІДОВНО: наступна — лише після відповіді попередньої.
 *
 * ⛔ Не паралельно: кожна частина — окрема транзакція запису на сервері (до ~6 с на 50 000 комірок), і пачка
 * одночасних таких транзакцій — рівно та ціна, від якої стеля сервера захищає.
 *
 * ⛔ Наступна частина звіряється зі СХОВИЩЕМ перед надсиланням (рецензія L10, M2): їде лише комірка, що досі
 * незбережена з ТИМ САМИМ значенням. Скасована (Ctrl+Z) чи перебита новим введенням, доки йшли попередні
 * частини, — не їде: інакше сервер на мить отримав би вже скасоване значення (зайвий запис аудиту й перерахунок),
 * а нове значення однаково поїхало б слідом. Нове значення везе звичайне автозбереження, щойно звільняться рядки.
 *
 * ⛔ Перша відмова ЗУПИНЯЄ надсилання. Прийняте раніше лишається прийнятим (частина — окремий атомарний запит;
 * відкотити її означало б іще один запис поверх, і він міг би впасти так само), а решта — НЕ губиться: усі правки
 * пакета вже лежать у сховищі (`pendingStore`), успіх частини знімає зі сховища лише її правки
 * (`discardPendingRows` у `send`), тож ненадіслане лишається на екрані незбереженим, із тим самим шляхом
 * довезення, що й у будь-якої відмови (`V-01`: «Retry save», діалог конфлікту, автозбереження). Скільки
 * збережено і скільки ні — людині кажуть прямо (`reportParts`), і цей лік гасне, щойно решту довезено.
 *
 * ⛔ Документ закрито посеред пакета (сховище скинуто — людина в діалозі виходу обрала «піти» з незбереженим) —
 * решта НЕ їде: без сховища немає ні позначки підтвердження, ні причини ручного значення, тож наступна частина
 * пішла б не тим запитом, яким людина її бачила. Сказано прямо, скільки збережено.
 *
 * ⚠ Історія Undo від цього не розходиться з екраном: крок вставки один на весь пакет, а екран показує ВСІ
 * вставлені значення — збережені й незбережені. Ctrl+Z повертає обидві групи (збережене — новим записом,
 * незбережене — заміною в сховищі), тобто рівно те, що людина бачить.
 *
 * @param send Надсилає ОДНУ частину і повертає, що з нею сталося (відмову показує сам — банер сітки чи тост).
 */
export async function saveInParts(
  edits: readonly PendingEdit[],
  send: (part: PendingEdit[]) => Promise<PartResult>,
  options: SaveInPartsOptions,
): Promise<PartsOutcome> {
  const parts = splitIntoParts(edits, options.max);
  const total = edits.length;
  const id = sliceIdOf(options.tableInstanceId, options.periodKey);

  if (parts.length <= 1) {
    const result = parts.length === 0 ? 'saved' : await send(parts[0] ?? []);
    const saved = result === 'saved' ? total : 0;

    return { total, saved, superseded: 0, parts: parts.length, failedPart: result === 'saved' ? null : 0, stoppedBy: stopOf(result) };
  }

  // Новий пакет частинами в тому самому зрізі — старий лік «не збережено» вже не про те.
  clearPartialNotice(id);
  const report = reportParts(total);
  const document = currentDocumentId();
  running += 1;

  try {
    // Рядки, уже надіслані попередніми частинами: «Keep mine» для них застарів — версію підняла відповідь.
    const sentRows = new Set<string>();
    let saved = 0;
    let superseded = 0;

    for (const [index, raw] of parts.entries()) {
      let part = raw;

      if (index > 0) {
        if (currentDocumentId() !== document) {
          const outcome = finish(total, saved, superseded, parts.length, index, 'left');
          report.stopped(outcome);
          return outcome;
        }

        const still = stillPending(raw, options.tableInstanceId, options.periodKey);
        superseded += raw.length - still.length;
        part = withKnownVersions(still, options.slice(), withoutRows(options.versionOverrides, sentRows));
        if (part.length === 0) {
          report.progress(saved, total - superseded);
          continue;
        }
      }

      for (const edit of part) sentRows.add(edit.rowKey);

      const result = await send(part);
      if (result !== 'saved') {
        const outcome = finish(total, saved, superseded, parts.length, index, stopOf(result));
        const unsent = parts.slice(index).flat();
        report.stopped(outcome);
        if (saved > 0) showPartialNotice(id, options.tableInstanceId, options.periodKey, outcome, unsent);

        return outcome;
      }

      saved += part.length;
      report.progress(saved, total - superseded);
    }

    const outcome = finish(total, saved, superseded, parts.length, null, null);
    report.finished(outcome);

    return outcome;
  } finally {
    running -= 1;
  }
}

function finish(
  total: number,
  saved: number,
  superseded: number,
  parts: number,
  failedPart: number | null,
  stoppedBy: PartsOutcome['stoppedBy'],
): PartsOutcome {
  return { total, saved, superseded, parts, failedPart, stoppedBy };
}

function stopOf(result: PartResult): PartsOutcome['stoppedBy'] {
  return result === 'saved' ? null : result;
}

/** Правки частини, що досі лежать у сховищі з тим самим значенням (див. M2 у коментарі `saveInParts`). */
function stillPending(part: readonly PendingEdit[], tableInstanceId: number, periodKey: number): PendingEdit[] {
  const pending = pendingSlice(tableInstanceId, periodKey);

  return part.filter((edit) => {
    const current = pending.get(cellKey(edit));
    return current !== undefined && wasSent(edit, current);
  });
}

function withoutRows(
  overrides: ReadonlyMap<string, string> | undefined,
  rows: ReadonlySet<string>,
): ReadonlyMap<string, string> | undefined {
  if (overrides === undefined || rows.size === 0) return overrides;

  return new Map([...overrides].filter(([rowKey]) => !rows.has(rowKey)));
}

function sliceIdOf(tableInstanceId: number, periodKey: number): string {
  return `${String(tableInstanceId)}:${String(periodKey)}`;
}

/** Номер тоста ходу: два пакети частинами (дві сітки) не мають оновлювати один тост. */
let reportSequence = 0;

/**
 * Текст ходу — у ВВІЧЛИВІЙ живій області (`role="status"`).
 *
 * ⚠ Mantine 7 ставить кореню тоста `role="alert"` безумовно (після `...others` — проп не перебиває). Вкладена
 * область `status` робить ОНОВЛЕННЯ ліку ввічливими: найближча жива область визначає, як читалка оголошує зміну
 * в ній, — «збережено 50 000 з 120 000» не перериває людину на кожній частині.
 */
function politely(text: string): ReturnType<typeof createElement> {
  return createElement('span', { role: 'status', 'aria-live': 'polite', 'data-save-parts': '' }, text);
}

/**
 * Видимий хід збереження частинами — лише для пакета з кількох частин.
 *
 * ⚠ Тост, а не банер сітки: частинами зберігає й документ за розмонтовану сітку (`saveOrphanSlice`), де банера
 * немає. Причину відмови показує зберігач, як і для одного запиту; тут — лише лік.
 */
function reportParts(total: number): {
  progress(saved: number, of: number): void;
  stopped(outcome: PartsOutcome): void;
  finished(outcome: PartsOutcome): void;
} {
  reportSequence += 1;
  const id = `ecr-patch-parts-${String(reportSequence)}`;
  const progressText = (saved: number, of: number): ReturnType<typeof createElement> =>
    politely(t('grid.saveParts.progress', { saved: formatNumber(saved), total: formatNumber(of) }));

  notifications.show({ id, loading: true, autoClose: false, withCloseButton: false, message: progressText(0, total) });

  return {
    progress(saved, of) {
      notifications.update({ id, loading: true, autoClose: false, withCloseButton: false, message: progressText(saved, of) });
    },
    stopped(outcome) {
      // Лік ходу гасне завжди: що збережено і що ні, далі каже окремий тост зрізу (або зберігач, якщо не збережено
      // нічого — лік «0 з N» нічого не додав би до причини).
      notifications.hide(id);

      if (outcome.stoppedBy === 'left') {
        notifications.show({
          color: 'statusWarning',
          autoClose: false,
          closeButtonProps: notificationCloseButtonProps,
          message: t('grid.saveParts.stopped', {
            saved: formatNumber(outcome.saved),
            total: formatNumber(outcome.total - outcome.superseded),
            rest: formatNumber(outcome.total - outcome.superseded - outcome.saved),
          }),
        });
      }
    },
    finished(outcome) {
      notifications.update({
        id,
        loading: false,
        color: 'statusSuccess',
        autoClose: 4000,
        withCloseButton: true,
        closeButtonProps: notificationCloseButtonProps,
        message: politely(t('grid.saveParts.done', { total: formatNumber(outcome.saved), parts: formatNumber(outcome.parts) })),
      });
    },
  };
}

/**
 * Тост «збережено X з Y, решта — ні» — ОДИН на зріз (рецензія L10, M1).
 *
 * ⛔ Доти він висів без автозакриття з новим id щоразу: Retry чи автозбереження довозили решту, а тост і далі
 * казав «не збережено», а наступна часткова відмова додавала поруч другий із іншими числами. Тепер тост живе,
 * доки в сховищі лишається бодай одна комірка з ненадісланої решти (з тим самим значенням), лік оновлюється на
 * кожну зміну сховища, і тост гасне, щойно решти не лишилось (довезено, скасовано чи відкинуто людиною).
 */
const partialNotices = new Map<string, () => void>();

function partialNoticeId(id: string): string {
  return `ecr-patch-parts-partial-${id}`;
}

function clearPartialNotice(id: string): void {
  const stop = partialNotices.get(id);
  if (stop === undefined) return;

  partialNotices.delete(id);
  stop();
}

function showPartialNotice(
  id: string,
  tableInstanceId: number,
  periodKey: number,
  outcome: PartsOutcome,
  unsent: readonly PendingEdit[],
): void {
  clearPartialNotice(id);

  const noticeId = partialNoticeId(id);
  const total = outcome.total - outcome.superseded;
  const waiting = outcome.stoppedBy === 'waiting';
  const message = (rest: number, first: boolean): string => {
    const params = {
      saved: formatNumber(total - rest),
      total: formatNumber(total),
      rest: formatNumber(rest),
    };
    if (!first) return t('grid.saveParts.stillUnsaved', params);

    return waiting ? t('grid.saveParts.waiting', params) : t('grid.saveParts.partial', params);
  };

  let rest = stillPending(unsent, tableInstanceId, periodKey).length;
  if (rest === 0) return;

  notifications.show({
    id: noticeId,
    ...(waiting ? {} : { color: 'statusWarning' }),
    autoClose: false,
    closeButtonProps: notificationCloseButtonProps,
    message: message(rest, true),
  });

  const unsubscribe = subscribePending(() => {
    const now = stillPending(unsent, tableInstanceId, periodKey).length;
    if (now === rest) return;

    rest = now;
    if (rest === 0) {
      clearPartialNotice(id);
      return;
    }

    notifications.update({
      id: noticeId,
      ...(waiting ? {} : { color: 'statusWarning' }),
      autoClose: false,
      closeButtonProps: notificationCloseButtonProps,
      message: message(rest, false),
    });
  });

  partialNotices.set(id, () => {
    unsubscribe();
    notifications.hide(noticeId);
  });
}
