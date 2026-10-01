import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import type { MappingPreview, SourceEntityStatus } from '@/api/types';
import { EntityPipeline } from '@/features/pipeline/EntityPipeline';
import { loadCatalog } from '@/shared/i18n';
import { theme } from '@/shared/theme/theme';
import { withTestDefaults } from '@/test/render';

/**
 * Конвеєр сутності на екрані (`ФВ-14.3`, область 9; `B21` §7).
 *
 * ⛔ Перевіряється те, заради чого екран існує: після кожного кроку видно
 * реальну кількість точок, крок, що звузив набір до нуля, підсвічений і
 * пояснений, а редагування (розклад, мапінги) — на місці, наявними
 * компонентами й наявними ендпоінтами.
 */
const Strings: Record<string, string> = {
  'pipeline.title': 'Data pipeline',
  'pipeline.intro': 'Last {days} days.',
  'pipeline.points': 'Rows out: {points}',
  'pipeline.narrowedTitle': 'Data narrows to zero here',
  'pipeline.zero.map': 'Collected rows fall under no active mapping.',
  'pipeline.zero.collect': 'Nothing was collected.',
  'pipeline.zero.emit': 'Nothing lands in a document.',
  'pipeline.step.schedule': 'Schedule',
  'pipeline.step.map': 'Mapping',
  'schedule.none': 'No schedule',
  'mapping.create': 'Add mapping',
};

const Entity = {
  id: 1,
  code: 'FLARE_01',
  displayName: 'Flare 01',
  entityPath: '\\\\AF\\Plant\\Flare_01',
  dataSourceId: 3,
  dataSourceCode: 'PI-WEST',
  transport: 'PiWebApi',
  isActive: true,
  lastRun: { status: 'Succeeded', finishedAt: '2026-09-30T01:00:00Z', pointsRetrieved: 40 },
  oldestGap: null,
  onMissingInSource: 'Keep',
  validFromAttribute: null,
  validToAttribute: null,
} as unknown as SourceEntityStatus;

/** 40 зібраних точок під єдиний, але ПРИЗУПИНЕНИЙ мапінг — набір звужується на мапінгу. */
const Preview: MappingPreview = {
  sourceEntityId: 1,
  code: 'FLARE_01',
  displayName: 'Flare 01',
  fromUtc: '2026-09-23T00:00:00Z',
  toUtc: '2026-09-30T00:00:00Z',
  pointsSeen: 40,
  isTruncated: false,
  fields: [
    {
      fieldMapId: 11,
      sourceField: 'Flare_01_CO',
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 100,
      targetColumnCode: 'CO_MASS',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 40,
      foldedValue: '42.5',
      isActive: false,
      pendingSourceUnitChange: null,
    },
  ],
  rows: [],
  unmappedSourceFields: [],
  uncoveredColumns: [],
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function stub(): string[] {
  const urls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      urls.push(url);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/mapping/preview')) return json(Preview);
      if (url.includes('/api/v1/collection-schedules')) return json([]);

      return json([]);
    }),
  );

  return urls;
}

async function show(allowed: boolean, entity: SourceEntityStatus = Entity): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <EntityPipeline entity={entity} allowed={allowed} sourcesHref="/admin/sources" />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const step = (key: string): HTMLElement => {
  const found = document.querySelector<HTMLElement>(`[data-step="${key}"]`);

  if (found === null) throw new Error(`крок ${key} не намальовано`);

  return found;
};

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 60_000;

describe('EntityPipeline', () => {
  it(
    'ФВ-14.3: точки після кожного кроку й підсвітка кроку, що звузив набір до нуля',
    async () => {
      const urls = stub();
      await show(true);

      await waitFor(() => expect(document.querySelector('[data-step="emit"]')).not.toBeNull());

      expect(step('collect').querySelector('[data-step-points]')?.getAttribute('data-step-points')).toBe('40');
      expect(step('map').querySelector('[data-step-points]')?.getAttribute('data-step-points')).toBe('0');

      // Лише мапінг підсвічений — і пояснений текстом, а не самим кольором.
      expect(step('collect').getAttribute('data-narrowed')).toBe('false');
      expect(step('map').getAttribute('data-narrowed')).toBe('true');
      expect(step('emit').getAttribute('data-narrowed')).toBe('false');
      expect(within(step('map')).getByText('Collected rows fall under no active mapping.')).toBeTruthy();
      expect(step('emit').querySelector('[data-step-state]')?.getAttribute('data-step-state')).toBe('idle');

      // Перегляд — на тих самих ендпоінтах, що й `/admin/mapping`.
      expect(urls.some((url) => url.includes('/api/v1/sources/1/mapping/preview?'))).toBe(true);
      expect(urls.some((url) => url.includes('/api/v1/collection-schedules?dataSource=PI-WEST'))).toBe(true);
    },
    Slow,
  );

  it(
    'редагування на місці: розклад і «завести мапінг» у своїх кроках',
    async () => {
      stub();
      await show(true);

      expect(await within(step('schedule')).findByText('No schedule')).toBeTruthy();
      await waitFor(() => expect(within(step('map')).getByRole('button', { name: 'Add mapping' })).toBeTruthy());
    },
    Slow,
  );

  it(
    'неактивна сутність: перегляд не запитується (сервер дав би 404), кроки 3–5 idle',
    async () => {
      const urls = stub();
      await show(true, { ...Entity, isActive: false });

      await waitFor(() => expect(document.querySelector('[data-step="emit"]')).not.toBeNull());

      for (const key of ['collect', 'map', 'emit']) {
        expect(step(key).querySelector('[data-step-state]')?.getAttribute('data-step-state')).toBe('idle');
      }
      expect(step('source').querySelector('[data-step-state]')?.getAttribute('data-step-state')).toBe('off');
      expect(urls.some((url) => url.includes('/mapping/preview'))).toBe(false);
    },
    Slow,
  );

  it(
    'без Integration.Manage кнопки заведення мапінгу немає',
    async () => {
      stub();
      await show(false);

      await waitFor(() => expect(document.querySelector('[data-step="map"]')).not.toBeNull());
      expect(within(step('map')).queryByRole('button', { name: 'Add mapping' })).toBeNull();
    },
    Slow,
  );
});
