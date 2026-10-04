import { vi } from 'vitest';
import { render, type RenderResult } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { RegistryBatchItem, RegistryBatchResult, RegistryRow } from '@/features/registries/rows/api';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { RegistryDataPage } from '../RegistryDataPage';

/**
 * Спільний стенд тестів редактора даних довідника (`ФВ-8.12`).
 *
 * ⚠ Рядки каталогу — САМ `09-seed.sql` (`Catalog` набору доступності), а не копія тут: тест бачить
 * ті самі тексти, що й користувач, і ключ, забутий у сіді, дає на екрані `⟦…⟧`.
 */
export const SeededStrings: Record<string, string> = Catalog;

export const registries = [
  { id: 1, code: 'STREAM_CASE', nameL10n: { values: { en: 'Stream cases' } }, fields: [], isTemporal: false, isHierarchical: false, sourceKind: 'Master' },
  { id: 2, code: 'STREAM', nameL10n: { values: { en: 'Flare streams' } }, fields: [], isTemporal: false, isHierarchical: false, sourceKind: 'Master' },
];

const fieldDto = (id: number, code: string, en: string, dataType: string, isRequired: boolean, lookup: number | null = null) => ({
  id,
  code,
  dataType,
  isRequired,
  isScopeField: false,
  lookupRegistryDefId: lookup,
  nameL10n: { values: { en } },
  unitId: null,
});

export const definition = {
  id: 1,
  code: 'STREAM_CASE',
  codeMode: 'Auto',
  dataRevision: 3,
  definitionVersion: 2,
  isTemporal: false,
  nameL10n: { values: { en: 'Stream cases' } },
  sourceKind: 'Master',
  mappings: [],
  relations: [],
  rules: [],
  fields: [
    fieldDto(11, 'STREAM', 'Stream', 'Lookup', true, 2),
    fieldDto(12, 'CASE_NAME', 'Case name', 'String', true),
    fieldDto(13, 'T_C', 'Temperature', 'Decimal', false),
  ],
  keys: [
    { id: 5, code: 'PK', fieldCodes: ['STREAM', 'CASE_NAME'], ignoreCase: true, isActive: true, isPrimary: true, nameL10n: { values: { en: 'Primary' } } },
  ],
};

export const storedRows: RegistryRow[] = [
  {
    id: 4411,
    code: 'E000004411',
    display: '1D-2 · 370 Winter',
    parentEntryId: null,
    validFrom: null,
    validTo: null,
    version: 'AAABkWmN3kM=',
    values: {
      STREAM: { value: '162', display: '1D-2 · HP Separator Gas', unit: null },
      CASE_NAME: { value: '370 Winter', display: null, unit: null },
      T_C: { value: '49.9999977539011', display: null, unit: 'degC' },
    },
  },
  {
    id: 4412,
    code: 'E000004412',
    display: '1D-2 · 370 Summer',
    parentEntryId: null,
    validFrom: null,
    validTo: null,
    version: 'AAABkWmN3kQ=',
    values: {
      STREAM: { value: '162', display: '1D-2 · HP Separator Gas', unit: null },
      CASE_NAME: { value: '370 Summer', display: null, unit: null },
    },
  },
];

/** Надісланий пакет: `dryRun` і рядки. */
export interface SentBatch {
  readonly dryRun: boolean;
  readonly items: RegistryBatchItem[];
}

export interface ServerOptions {
  readonly permissions?: string[];
  /** Відповідь на пакет; за замовчуванням — усе пройшло. */
  readonly batch?: (sent: SentBatch) => RegistryBatchResult;
  readonly rows?: RegistryRow[];
  /** Збереження (не `dryRun`) відповідає лише після цього проміса — «запит у дорозі». */
  readonly holdCommit?: Promise<unknown>;
  /** Статус відповіді на рядки довідника-цілі `STREAM` (зіставлення `Lookup`); за замовчуванням 200. */
  readonly lookupStatus?: number;
  /** Сеанс симуляції «очима користувача» (`/me.isSimulation`). */
  readonly simulation?: boolean;
}

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

export function passed(sent: SentBatch): RegistryBatchResult {
  return {
    applied: !sent.dryRun,
    dryRun: sent.dryRun,
    added: 0,
    updated: sent.items.length,
    deleted: 0,
    unchanged: 0,
    rows: sent.items.map((item) => ({ clientRowId: item.clientRowId, entryId: item.id, errors: [], status: 'updated', version: 'next' })),
  };
}

/** Підміняє мережу; повертає журнал надісланих пакетів. */
export function mockServer(options: ServerOptions = {}): SentBatch[] {
  const sent: SentBatch[] = [];
  const me = {
    denies: [],
    grants: {},
    isSimulation: options.simulation === true,
    language: 'en',
    mustChangePassword: false,
    permissions: options.permissions ?? ['Registry.View', 'Registry.EditData'],
    simulatedForUserId: null,
    userId: 1,
    userName: 'tester',
  };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      if (url.includes('/api/v1/me')) return json(me);
      if (method === 'POST' && url.includes('/entries/batch')) {
        const batch: SentBatch = {
          dryRun: url.includes('dryRun=true'),
          items: (JSON.parse(String(init?.body)) as { items: RegistryBatchItem[] }).items,
        };
        sent.push(batch);
        if (!batch.dryRun && options.holdCommit !== undefined) await options.holdCommit;
        return json((options.batch ?? passed)(batch));
      }
      if (url.includes('/definition')) return json(definition);
      if (url.includes('/api/v1/registries/STREAM/rows')) {
        if (options.lookupStatus !== undefined && options.lookupStatus >= 400) {
          return new Response(JSON.stringify({ title: 'Forbidden', status: options.lookupStatus, errorCode: 'ECR-AUTH-0403' }), {
            status: options.lookupStatus,
            headers: { 'Content-Type': 'application/problem+json' },
          });
        }
        return json({ items: [{ ...storedRows[0], id: 162, code: 'S162', display: '1D-2 · HP Separator Gas', values: {} }], nextCursor: null, totalCount: 1 });
      }
      if (url.includes('/rows')) {
        const items = options.rows ?? storedRows;
        return json({ items, nextCursor: null, totalCount: items.length });
      }
      if (url.endsWith('/api/v1/registries')) return json(registries);
      if (url.includes('/api/v1/units')) return json([]);

      return json(null);
    }),
  );

  return sent;
}

export function showDataPage(): RenderResult {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/registries/STREAM_CASE/entries']}>
          <Routes>
            <Route path="/admin/registries/:code/entries" element={<RegistryDataPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}
