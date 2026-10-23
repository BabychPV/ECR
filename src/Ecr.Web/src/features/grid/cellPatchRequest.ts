import type { QueryClient } from '@tanstack/react-query';
import { apiFetch, isSessionClosed, sessionUserHeaders } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { PatchCell, PatchCellsRequest, PatchCellsResponse, TableSliceDto } from '@/api/types';
import { applyPatchToSlice } from './sliceApply';
import { hasConfirmed } from './confirmedEdits';
import { calculationResultsKey } from '@/features/methodologies/calculationResultsKey';

/*
 * ⚠ Запит патча комірок і його застосування до кешу — окремим модулем від `useCellPatch.ts` (`D-132`).
 * Їх потребує автозбереження рівня документа (`autosave.ts`), тобто статичний граф сторінки документа;
 * хук сітки, стеження за перерахунком і підписи конфліктів — код сітки, що живе в її лінивому чанку.
 * `useCellPatch.ts` реекспортує все звідси: наявні імпорти й тести не змінюються.
 */

/** Накопичена зміна однієї комірки. */
export interface PendingEdit {
  rowKey: string;
  columnCode: string;
  /** Значення; `null` — стерти комірку (R-B4). */
  value: unknown;
  /** Явна порожнеча: користувач свідомо лишив комірку порожньою. */
  isEmpty: boolean;
  /** Версія рядка на момент читання; `null` — створення рядка (R-B2). */
  baseVersion: string | null;
  /**
   * Значення комірки, яке людина БАЧИЛА, коли вводила (`null` — порожньо);
   * `undefined` — невідоме (шлях без знімка). Лише для клієнта, на сервер не їде:
   * за ним `withKnownVersions` упізнає чужу правку тієї самої комірки (L8-20).
   */
  before?: unknown;
  /**
   * `G1-06`: значення текстової колонки — звіряти ДОСЛІВНО (`0012` ≠ `12`).
   * Лише для клієнта, на сервер не їде; відсутнє — правило за значенням.
   */
  text?: boolean;
}

/**
 * Збирає накопичені зміни в один запит.
 *
 * ⚠ Надсилаються **лише змінені** комірки. Повний зріз 500×60 на кожне
 * збереження — це тридцять тисяч комірок замість трьох, і саме він не
 * вкладається в бюджет 300 мс на сотню.
 *
 * ⚠ Три різні операції (R-B4) розрізняються явно: значення — записати,
 * `value: null` — стерти, `isEmpty: true` — явна порожнеча. Це різні наміри,
 * і злити їх в один означало б втратити відмінність між «тут нуль», «тут ще
 * не заповнювали» і «тут свідомо порожньо».
 */
export function buildRequest(
  tableInstanceId: number,
  periodKey: number,
  edits: readonly PendingEdit[],
  origin = 'UserEdit',
): PatchCellsRequest {
  const rows = new Map<string, { baseVersion: string | null; cells: PatchCell[] }>();

  for (const edit of edits) {
    const row = rows.get(edit.rowKey) ?? { baseVersion: edit.baseVersion, cells: [] };

    // ⚠ `value` присутнє ЗАВЖДИ, навіть при явній порожнечі. Контракт
    // розрізняє три наміри прапорцем `isEmpty`, а не наявністю поля: сама
    // комірка потрапляє в запит лише тоді, коли її змінюють, і «поля немає»
    // означало б «не чіпати» — тобто порожній намір у списку змін.
    row.cells.push({
      columnCode: edit.columnCode,
      value: edit.isEmpty ? null : edit.value,
      isEmpty: edit.isEmpty,
    });

    rows.set(edit.rowKey, row);
  }

  return {
    tableInstanceId,
    periodKey,
    origin,
    // ⛔ `ФВ-2.16`: сервер відхиляє батч із коміркою `AllowWithConfirmation`
    // без цього прапорця. Ставиться ЛИШЕ коли в батчі є правка, яку людина
    // справді підтвердила (`confirmedEdits.ts`), — і тут, а не в кожному
    // викликачі: запит будують три шляхи, і жоден не має права його забути.
    ...(hasConfirmed(tableInstanceId, periodKey, edits) ? { confirmed: true } : {}),
    rows: [...rows].map(([rowKey, row]) => ({
      rowKey,
      baseVersion: row.baseVersion,
      cells: row.cells,
    })),
  };
}

/**
 * Надсилає пакет правок і повертає відповідь сервера.
 *
 * ⚠ Адреса несе ДОКУМЕНТ, а не лише екземпляр таблиці: маршрут контракту —
 * `PATCH /api/v1/documents/{documentId}/cells`. До аудиту клієнт бив у
 * `/api/v1/cells`, якого не існує, і збереження не працювало взагалі
 * (`A7-03`).
 *
 * ⛔ Окрема функція, а не тіло хука, саме тому, що надсилати доводиться й
 * ЗВІДКИ, де хука немає: зріз, чия сітка вже розмонтована перемиканням аркуша,
 * зберігає рівень документа (`autosave.ts`, `D14-12`). Доки цей `fetch` жив
 * усередині `useCellPatch`, «зберегти може лише змонтована сітка» було не
 * рішенням, а наслідком розташування коду.
 */
export function patchCells(
  documentId: number,
  request: PatchCellsRequest,
): Promise<PatchCellsResponse> {
  return apiFetch<PatchCellsResponse>(`/api/v1/documents/${documentId}/cells`, {
    method: 'PATCH',
    body: JSON.stringify(request),
  });
}

/**
 * Застосовує відповідь на патч до кешу зрізу — БЕЗ жодного запиту.
 *
 * ⛔ `CL-01`: тут стояв `invalidateQueries` зрізу — після КОЖНОГО успішного
 * збереження, тобто автозбереження коштувало `PATCH` плюс найважчий `GET`
 * системи. І мети він не досягав: перерахунок асинхронний, тож відповідь на
 * перезапит приходила здебільшого РАНІШЕ за нього, зі старими обчисленими
 * значеннями.
 *
 * ⚠ Тепер відповідь застосовується локально (`sliceApply.ts`): власні значення
 * оператора, нові `rowVersions`, округлення вставки — усе це вже є в запиті й
 * відповіді, і жодного запиту не потрібно. Обчислені колонки принесе
 * перерахунок; стежить за ним `useRecalculationStatus` нижче — по
 * `recalculationJobId` з відповіді (`BE-05`).
 *
 * ⛔ І стеження НЕ повертає сюди `invalidateQueries` зрізу. Опитування задачі
 * і перезапит зрізу — різні за ціною речі: перше коштує кілька сотень байтів
 * раз на дві секунди, друге — найважчий `GET` системи. Інвалідація на КОЖНЕ
 * опитування відкотила б `CL-01…03` рівно туди, звідки їх витягли.
 *
 * ⛔ Викликається і для зрізу, чия сітка вже розмонтована (`D14-12`): інакше
 * повернення на аркуш показувало б із кешу СТАРЕ значення — збережене на
 * сервері, але невидиме, тобто рівно той симптом, від якого лікує сховище.
 */
export function applyPatchLocally(
  queryClient: QueryClient,
  request: PatchCellsRequest,
  response: PatchCellsResponse,
): void {
  const key = queryKeys.slices.one(request.tableInstanceId, request.periodKey);

  // ⛔ `G1-01`: `GET` зрізу, що почався ДО коміту цього `PATCH` (перезапит після
  // перерахунку, `addRow`, «Keep mine»), `setQueryData` НЕ скасовує: його
  // відповідь, прочитана зі старої БД, перезаписала б кеш старими значеннями й
  // СТАРИМИ версіями рядків — значення «зникає» з екрана, а наступна власна
  // правка рядка дістає `409`. Тому, якщо зріз саме читається, після
  // локального застосування читання ПЕРЕЗАПУСКАЄТЬСЯ: `refetchType: 'all'`
  // скасовує запит у дорозі (`cancelRefetch`, тихо — без стану помилки) і
  // ставить новий, що читає вже після коміту. Зайвий `GET` — лише тоді, коли
  // вікна справді перекрилися; у звичайному разі `CL-01` лишається без запиту.
  const racing = queryClient.getQueryState(key)?.fetchStatus === 'fetching';

  queryClient.setQueryData<TableSliceDto>(key, (slice) =>
    slice === undefined ? slice : applyPatchToSlice(slice, request, response),
  );

  // ⚠ І зріз позначається застарілим — БЕЗ запиту (`refetchType: 'none'`).
  // Судження, яке варто назвати вголос: локальне застосування не знає
  // обчислених колонок, а `staleTime` зрізів — 5 хв (`CL-02`), тож без цього
  // рядка результат перерахунку не з'явився б до перезаходу. Так він
  // з'явиться при наступному монтуванні сітки (перемикання аркуша), не
  // коштуючи жодного запиту зараз.
  //
  // ⛔ Саме ПІСЛЯ `setQueryData`: успішний запис у кеш скидає позначку
  // `isInvalidated`, тож зворотний порядок нічого б не позначив.
  void queryClient.invalidateQueries({
    queryKey: key,
    refetchType: racing ? 'all' : 'none',
  });

  // ⛔ `ФВ-2.16`: значок «поза вікном» — за `import()`, не статично (`D-132`,
  // коментар `outOfWindowMarks.ts`); порожня відповідь модуля не тягне.
  const outOfWindow = response.outOfWindow ?? [];
  if (outOfWindow.length > 0) {
    void import('./outOfWindowMarks').then((marks) =>
      marks.recordOutOfWindow(request.tableInstanceId, request.periodKey, outOfWindow),
    );
  }
}

/**
 * RC15-C: після збереження правки картка документа перечитується, коли банер «результати застаріли»
 * ще НЕ показано (`resultsStale === false`): сервер виводить ознаку з часу правки входу, і без цього банер
 * з'являвся лише після перезавантаження сторінки.
 *
 * ⛔ Лише перехід `false` → `true`, а не кожне збереження: `resultsStale === true` уже показано, `null`
 * (читач із звуженим доступом) і відсутня картка — «не знаємо», запит не потрібен. Так автозбереження не
 * платить додатковим `GET` картки щоразу (`CL-01`). Числа методологій (бейдж у шапці, один ключ із панеллю)
 * перечитуються разом із карткою, щоб бейдж і банер не розходилися.
 */
export function refreshStaleness(queryClient: QueryClient, documentId: number, periodKey: number): void {
  const summaryKey = ['document', documentId, periodKey] as const;
  const summary = queryClient.getQueryData<{ resultsStale?: boolean | null }>(summaryKey);
  const becameStale = summary !== undefined && summary.resultsStale === false;

  // ⛔ AN-108 / P2-01: «Needs recalculation (N)» змінюється лише переходом ЦЬОГО документа false → true. Інакше —
  // лише позначка «застаріле» без запиту: зведення (найдорожчий агрегат переліку) перечитається при монтуванні
  // переліку. Безумовний перезапит тут давав `GET /documents/summary` на КОЖНЕ автозбереження через бейдж меню.
  void queryClient.invalidateQueries({
    queryKey: ['documents', 'summary', periodKey],
    refetchType: becameStale ? 'active' : 'none',
  });
  if (!becameStale) return;

  void queryClient.invalidateQueries({ queryKey: summaryKey, exact: true });
  void queryClient.invalidateQueries({ queryKey: calculationResultsKey(documentId, periodKey), exact: true });
}

/**
 * Надсилає останній пакет правок при закритті вкладки (`B-35`, `#38`).
 *
 * ⛔ Звичайний `apiFetch` тут не підходить: `beforeunload` не чекає на
 * `await`, сторінка вивантажується незалежно від того, дійшла відповідь чи
 * ні. `keepalive: true` — єдиний прапорець `fetch`, який браузер шанує саме
 * в цей момент: запит триває й після того, як документ зник, за умови, що
 * тіло вкладається в ліміт (≈64 КБ) — а пакет правок одного зрізу в нього
 * вкладається з великим запасом.
 *
 * ⚠ Відповідь навмисно ІГНОРУЄТЬСЯ: обробляти конфлікт чи оновлювати версії
 * рядків тут нема кому — вкладка вже зачиняється, а екран, який показав би
 * результат, зникає раніше за нього.
 *
 * ⛔ Відмова гаситься ЯВНО, і це не обробка помилки — це визнання, що
 * адресата в неї немає. Проковтнути тут чесно рівно тому, що інших
 * варіантів не існує: банер показати нема на чому (документ вивантажується в
 * цю саму мить), консоль зникає разом із вкладкою, а окремого логера чи
 * телеметрії, куди можна було б донести факт, у проєкті немає взагалі — і
 * заводити його тут, у гілці, яка виконується під час знищення сторінки,
 * означало б завести другий такий самий запит, який так само не встигне.
 * Альтернатива ж — лишити відхилення без обробника — не «нічого не робить»:
 * вона дає `unhandledrejection`, тобто шум, якого ніхто не читає, і ризик,
 * що обробник цієї події (свій чи чужий) зробить на вивантаженні щось іще.
 */
export function sendPatchBeacon(documentId: number, request: PatchCellsRequest): void {
  // ⚠ Два РІЗНІ шляхи відмови, і `try/catch` покриває лише перший:
  //   • синхронний кидок `fetch` (некоректний URL, заборона політикою) —
  //     ловиться `catch` нижче;
  //   • відхилення проміса (мережа впала, з'єднання обірване вивантаженням
  //     сторінки) — до `catch` не доходить ніколи, бо його вже немає в стеку.
  // Тому потрібні обидва запобіжники, а не один.
  const swallow = (): void => {
    // Навмисно порожньо — причина в коментарі до функції.
  };

  // ⛔ AN-108 / S2-05: сеанс вкладки закрито (вихід або інший користувач у сусідній вкладці) — cookie вже не
  // власника цих правок. Маячок не обходить `apiFetch`, тож перевірка тут власна; правки лишились у сліді
  // `lostEdits` під їхнім власником.
  if (isSessionClosed()) return;

  try {
    fetch(`/api/v1/documents/${documentId}/cells`, {
      method: 'PATCH',
      // ⛔ AN-108 / S2-05: маячок іде повз `apiFetch` — id користувача вкладки додається тут; cookie іншого
      // користувача сервер відхилить (`409 ECR-AUTH-0409`), а не запише правки під його іменем.
      headers: { 'Content-Type': 'application/json', ...sessionUserHeaders() },
      credentials: 'include',
      keepalive: true,
      body: JSON.stringify(request),
    }).catch(swallow);
  } catch {
    swallow();
  }
}
