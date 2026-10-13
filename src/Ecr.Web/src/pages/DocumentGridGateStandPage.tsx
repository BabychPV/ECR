import type { JSX } from 'react';
import type { PatchCellStylesRequest } from '@/api/types';
import { DocumentGrid } from '@/features/grid/DocumentGrid';

/**
 * Стенд швидкого вводу у СПРАВЖНЬОМУ `DocumentGrid` (T5-01) для
 * `e2e/keyCommitGateLive.spec.ts`.
 *
 * ⛔ Навіщо, коли є `/_key-commit-gate`. Тестувальник (прохід 5, main c4d64f41)
 * бачив на 50–80 мс значення в чужому рядку й втрачене значення, а голий
 * RevoGrid на тому самому інтервалі був чистий (0 зі 100 прогонів, CPU ×4).
 * Різниця — усе, що `DocumentGrid` робить ПІСЛЯ фіксації: `afteredit` →
 * стан React → нове джерело сітки (перерендер `revo-grid` і редактора), PATCH,
 * колонки-замикання. Ці шляхи стенд із голим RevoGrid не відтворює взагалі.
 *
 * ⚠ Сервер — у браузері: `fetch` на адреси цього документа обслуговує
 * пам'ять стенда (зріз, PATCH із затримкою `?latency=`, мс), решта адрес — як
 * завжди. Отримані PATCH видно у `window.__standPatches` — мірило «значення
 * пішло на сервер», окремо від DOM.
 *
 * ⚠ Лише для DEV (`router.tsx`, `devRoutes`), як `/_key-commit-gate`.
 */
const StandDocumentId = 9_001;
const StandTableInstanceId = 9_001;
const StandPeriodKey = 202_609;
export const StandRowCount = 8;

interface StandPatchRow {
  rowKey: string;
  cells: { columnCode: string; value: unknown }[];
}

interface StandWindow {
  __standPatches?: StandPatchRow[];
  __standFetchInstalled?: boolean;
}

const standWindow = window as unknown as StandWindow;

const stored = new Map<string, Record<string, unknown>>();

// L4: користувацький колір комірок ("{rowKey}:{columnCode}" → {backgroundHex, foregroundHex}).
const styles = new Map<string, { backgroundHex: string | null; foregroundHex: string | null; isBold: boolean }>();

// D-1: `?calc=1` додає обчислювану колонку TOTAL = 2 × QTY. Як на справжньому сервері, вона
// відстає від правки: слайс віддає TOTAL лише з ЗАВЕРШЕНОЇ задачі перерахунку (~1.5 с після PATCH).
const calcTotals = new Map<string, number>();
const CalcLagMs = 1_500;
let calcJobSeq = 0;
let calcJobDoneAt = 0;

const calcEnabled = (): boolean => new URLSearchParams(window.location.search).get('calc') === '1';

function finishCalcJob(): void {
  for (const [rowKey, cells] of stored) {
    const qty = Number(cells['QTY']);
    if (Number.isFinite(qty)) calcTotals.set(rowKey, qty * 2);
  }
}

function column(code: string, ordinal: number, dataType: string, isReadOnly: boolean): Record<string, unknown> {
  return {
    code,
    dataType,
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  };
}

/** `?headerFill=c6d9f1` — стиль шапки таблиці (`TableSliceDto.headerStyle`) для e2e шапки в Dark. */
function headerStyle(): Record<string, unknown> | undefined {
  const fill = /^[0-9a-f]{6}$/i.exec(new URLSearchParams(window.location.search).get('headerFill') ?? '')?.[0];

  if (fill === undefined) return undefined;

  return {
    isBold: false, isItalic: false, wrapText: false, foregroundArgb: null, horizontalAlign: null, verticalAlign: null,
    backgroundArgb: (0xff000000 | Number.parseInt(fill, 16)) | 0,
  };
}

function slice(): Record<string, unknown> {
  return {
    headerStyle: headerStyle(),
    cellConfirmations: {},
    cellPermissions: {},
    cellStyles: Object.fromEntries(styles),
    periodKey: StandPeriodKey,
    tableInstanceId: StandTableInstanceId,
    columns: [
      column('CODE', 0, 'String', true),
      column('QTY', 1, 'Decimal', false),
      ...(calcEnabled() ? [column('TOTAL', 2, 'Decimal', true)] : []),
    ],
    rows: Array.from({ length: StandRowCount }, (_, index) => {
      const rowKey = `R${index + 1}`;

      return {
        cells: {
          CODE: rowKey,
          ...stored.get(rowKey),
          ...(calcEnabled() && calcTotals.has(rowKey) ? { TOTAL: calcTotals.get(rowKey) } : {}),
        },
        isOrphaned: false,
        label: null,
        ordinal: index,
        rowKey,
        rowVersion: `v${index}`,
        rowKind: 'Item',
      };
    }),
  };
}

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function installStandServer(): void {
  if (standWindow.__standFetchInstalled === true) return;
  standWindow.__standFetchInstalled = true;
  standWindow.__standPatches = [];

  const latency = Number(new URLSearchParams(window.location.search).get('latency') ?? '30');
  const original = window.fetch.bind(window);

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const path = new URL(url, window.location.origin).pathname;

    if (path === `/api/v1/documents/${StandDocumentId}/cells` && init?.method === 'PATCH') {
      const rows = (JSON.parse(String(init.body)) as { rows: StandPatchRow[] }).rows;
      standWindow.__standPatches?.push(...rows);
      for (const row of rows) {
        const cells = { ...stored.get(row.rowKey) };
        for (const cell of row.cells) cells[cell.columnCode] = cell.value;
        stored.set(row.rowKey, cells);
      }
      await new Promise((resolve) => setTimeout(resolve, latency));

      if (calcEnabled()) {
        calcJobSeq += 1;
        calcJobDoneAt = Date.now() + CalcLagMs;
        const jobId = `StandRecalcJob#${String(calcJobSeq)}`;
        window.setTimeout(finishCalcJob, CalcLagMs);

        return json({ appliedCells: rows.length, rowVersions: {}, validation: [], recalculationJobId: jobId });
      }

      return json({ appliedCells: rows.length, rowVersions: {}, validation: [] });
    }
    // ⚠ Без літерала адреси (як і нижче): шлях запису кольору перевіряє регулярка, не рядок.
    if (/\/documents\/\d+\/tables\/\d+\/cell-styles$/.test(path) && init?.method === 'PATCH') {
      // Фейк повторює КОНТРАКТ сервера (`PatchCellStylesRequest`, schema.d.ts), а не клієнтську вигадку:
      // без `periodKey` або з порожньою зміною — 422, як на справжньому сервері.
      const request = JSON.parse(String(init.body)) as Partial<PatchCellStylesRequest>;
      const items = request.items ?? [];
      const invalid =
        typeof request.periodKey !== 'number' ||
        (items.length === 0 && request.scope == null) ||
        items.length > 2000 ||
        (request.reset !== true && items.some((item) => item.backgroundHex == null && item.foregroundHex == null && item.isBold == null));
      if (invalid) return new Response(JSON.stringify({ errorCode: 'ECR-REQ-0422', title: 'Unprocessable' }), { status: 422, headers: { 'Content-Type': 'application/problem+json' } });

      const clearOrSet = (value: string | null | undefined, old: string | null): string | null =>
        value == null ? old : value === '' ? null : value;
      for (const item of items) {
        const key = `${item.rowKey}:${item.columnCode}`;
        const before = styles.get(key) ?? { backgroundHex: null, foregroundHex: null, isBold: false };
        const next =
          request.reset === true
            ? { backgroundHex: null, foregroundHex: null, isBold: false }
            : {
                backgroundHex: clearOrSet(item.backgroundHex, before.backgroundHex),
                foregroundHex: clearOrSet(item.foregroundHex, before.foregroundHex),
                isBold: item.isBold ?? before.isBold,
              };
        if (next.backgroundHex === null && next.foregroundHex === null && !next.isBold) styles.delete(key);
        else styles.set(key, next);
      }

      return json({ appliedCells: items.length, resetCells: 0 });
    }
    // ⚠ Без літерала адреси: сторож `EndpointCoverageTests` звіряє кожен шлях клієнта з сервером.
    if (/\/StandRecalcJob(%23|#)\d+$/.test(path)) {
      return json({ state: Date.now() >= calcJobDoneAt ? 'Succeeded' : 'Running' });
    }
    if (path === `/api/v1/documents/${StandDocumentId}/tables/${StandTableInstanceId}`) return json(slice());
    // ⛔ `/me/preferences` теж свій: у e2e-стенді зі справжнім API сторінка поза сесією отримувала 401 →
    // редірект на /login, і сітка не з'являлась (dev-сервер без API цього не бачив: там 500 без редіректу).
    if (path === '/api/v1/registries' || path === '/api/v1/units' || path === '/api/v1/me/preferences') return json([]);

    return original(input, init);
  };
}

export function DocumentGridGateStandPage(): JSX.Element {
  installStandServer();

  return (
    <div data-stand="document-grid-gate" style={{ width: 480 }}>
      <DocumentGrid
        documentId={StandDocumentId}
        tableInstanceId={StandTableInstanceId}
        tableDefId={StandTableInstanceId}
        periodKey={StandPeriodKey}
        periodEnd={null}
        // A2: `?readonly=1` - сітка лише для читання (закритий період), `e2e/gridF2Live.spec.ts`.
        readOnly={new URLSearchParams(window.location.search).get('readonly') === '1'}
        allowsDynamicRows={false}
        maxDynamicRows={null}
      />
    </div>
  );
}
