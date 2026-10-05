import type { JSX } from 'react';
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

function slice(): Record<string, unknown> {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: StandPeriodKey,
    tableInstanceId: StandTableInstanceId,
    columns: [column('CODE', 0, 'String', true), column('QTY', 1, 'Decimal', false)],
    rows: Array.from({ length: StandRowCount }, (_, index) => {
      const rowKey = `R${index + 1}`;

      return {
        cells: { CODE: rowKey, ...stored.get(rowKey) },
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

      return json({ appliedCells: rows.length, rowVersions: {}, validation: [] });
    }
    if (path === `/api/v1/documents/${StandDocumentId}/tables/${StandTableInstanceId}`) return json(slice());
    if (path === '/api/v1/registries' || path === '/api/v1/units') return json([]);

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
        readOnly={false}
        allowsDynamicRows={false}
        maxDynamicRows={null}
      />
    </div>
  );
}
