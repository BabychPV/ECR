import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { SourceEventsTab } from '../SourceEventsTab';
import { eventsQuery, formatInZone } from '../SourceEventsTable';

/**
 * Вкладка «Події з PI» (HSE301 A6-UI) проти заглушки `fetch`: що пішло на сервер (метод, адреса, тіло) і що
 * з відповіді доїхало до людини — стан «не налаштовано», рядки подій, «Показати ще» курсором, «Отримати з PI
 * зараз» з підказкою «створіть мапінг», пауза мапінгу і відмова видалення.
 */

interface Sent {
  readonly method: string;
  readonly path: string;
  readonly search: string;
  readonly body: unknown;
}

let sent: Sent[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function problem(status: number, errorCode: string, messageKey: string, extra: Record<string, unknown> = {}): Response {
  return json({ title: `err.${errorCode}`, status, errorCode, correlationId: 'c-1', detail: 'server detail', messageKey, ...extra }, status);
}

const Source = {
  catalog: null,
  code: 'PI-MAIN',
  collectionSchedules: 0,
  endpoint: 'Server=pi-main',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 1,
  transport: 'PiSqlClient',
};

const Entity = {
  code: 'FLARE_EVENTS',
  dataSourceCode: 'PI-MAIN',
  dataSourceId: 7,
  displayName: 'Flare events',
  entityPath: null,
  id: 42,
  isActive: true,
  lastRun: null,
  oldestGap: null,
  onMissingInSource: 'MarkOrphaned',
  registryDefId: null,
  transport: 'PiSqlClient',
  validFromAttribute: null,
  validToAttribute: null,
  validToInclusive: false,
};

const Map12 = {
  id: 12,
  sourceEntityId: 42,
  documentId: 100,
  tableDefId: 200,
  volumeMode: 'None',
  filterAttribute: null,
  filterScope: null,
  filterValue: null,
  isActive: true,
  fields: [
    { id: 1, targetColumnDefId: 1, sourceAttribute: '$start', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
    { id: 2, targetColumnDefId: 2, sourceAttribute: '$end', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
  ],
};

function eventRow(id: number, patch: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id,
    documentId: 100,
    documentKey: 'DOC-000123',
    startUtc: '2026-09-30T04:00:00Z',
    startLocal: '2026-09-30T09:00:00+05:00',
    endUtc: '2026-09-30T04:15:00Z',
    endLocal: '2026-09-30T09:15:00+05:00',
    eventName: `Flaring ${id}`,
    firstSeenAt: '2026-09-30T05:00:00Z',
    lastSeenAt: '2026-09-30T05:00:00Z',
    lastSyncAt: '2026-09-30T05:00:00Z',
    keptManual: [],
    periodKey: 202609,
    primaryElement: 'HP_FLARE_A',
    rowKey: `EF-${id}`,
    sourceEventId: `guid-${id}`,
    sourceEventMapId: 12,
    sourceModifiedUtc: null,
    status: 'Synced',
    tableInstanceId: 300,
    timeZoneId: 'Asia/Atyrau',
    unmapped: [],
    ...patch,
  };
}

interface Options {
  templates?: () => Response;
  maps?: unknown[];
  sync?: () => Response;
  remove?: () => Response;
}

function respond({
  templates = () => json([{ templateName: 'FLARE_EVENTS', attributes: [] }]),
  maps = [Map12],
  sync = () => json({ jobId: 'ISourceEventSyncJob-a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4' }, 202),
  remove = () => new Response(null, { status: 204 }),
}: Options = {}): void {
  sent = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;
      const method = init?.method ?? 'GET';
      const body: unknown = init?.body === undefined || init.body === null ? undefined : JSON.parse(String(init.body));
      sent.push({ method, path, search: url.search, body });

      if (path === '/api/v1/sources') return json([Entity]);
      if (path === '/api/v1/data-sources/7/event-templates') return templates();
      if (path === '/api/v1/source-event-maps') return json(maps);
      if (path === '/api/v1/row-window-maps') return json([]);
      if (path === '/api/v1/source-event-maps/12' && method === 'PUT') return json({ ...Map12, ...(body as object) });
      if (path === '/api/v1/source-event-maps/12' && method === 'DELETE') return remove();
      if (path === '/api/v1/documents') {
        return json({ items: [{ id: 100, businessKey: 'DOC-000123' }], nextCursor: null, totalCount: 1 });
      }
      if (path === '/api/v1/sources/42/source-events/sync') return sync();
      if (path === '/api/v1/sources/42/source-events') {
        return url.searchParams.get('cursor') === 'page-2'
          ? json({ items: [eventRow(2, { status: 'Missing' })], nextCursor: null, totalCount: 2 })
          : json({
              items: [eventRow(1, { keptManual: ['Category'], unmapped: [{ column: 'Season', value: 'зима' }] })],
              nextCursor: 'page-2',
              totalCount: 2,
            });
      }

      return json(null);
    }),
  );
}

function show(canManage = true): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SourceEventsTab source={Source as never} canManage={canManage} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function firstRow(): Promise<HTMLElement> {
  return waitFor(() => {
    const row = document.querySelector<HTMLElement>('[data-source-event-row="1"]');
    expect(row).not.toBeNull();
    return row as HTMLElement;
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('SourceEventsTab', () => {
  it('рядок події: стан, ключ EF-…, документ із періодом, колонки за людиною, незіставлене значення', async () => {
    respond();
    show();

    const row = await firstRow();

    expect(row.querySelector('[data-event-status="Synced"]')?.textContent).toBe('⟦sourceEvents.status.Synced⟧');
    expect(row.querySelector('[data-event-row-key]')?.textContent).toBe('EF-1');
    expect(row.querySelector('[data-event-element]')?.textContent).toBe('HP_FLARE_A');
    expect(row.querySelector('[data-event-document]')?.getAttribute('href')).toBe('/documents/100?periodKey=202609');
    expect(within(row.querySelector<HTMLElement>('[data-event-kept-manual]')!).getByText('Category')).toBeTruthy();
    expect(row.querySelector('[data-event-unmapped]')?.textContent).toContain('Season: зима');
    expect(document.querySelector('[data-source-events-total="2"]')).not.toBeNull();

    // Перший запит — без курсора, з розміром сторінки.
    const first = sent.find((s) => s.path === '/api/v1/sources/42/source-events');
    expect(first?.search).toBe('?limit=50');
  });

  it('«Показати ще» — наступна сторінка курсором, рядки дописуються, кнопка зникає на кінці', async () => {
    respond();
    show();
    await firstRow();

    fireEvent.click(await screen.findByRole('button', { name: '⟦sourceEvents.showMore⟧' }));

    await waitFor(() => expect(document.querySelector('[data-source-event-row="2"]')).not.toBeNull());
    expect(document.querySelector('[data-source-event-row="1"]')).not.toBeNull();
    expect(sent.some((s) => s.path === '/api/v1/sources/42/source-events' && s.search.includes('cursor=page-2'))).toBe(true);
    await waitFor(() => expect(document.querySelector('[data-source-events-more]')).toBeNull());
  });

  it('каталог не налаштовано (422 ECR-INT-0422) — інформаційний стан, а не помилка; таблиця подій лишається', async () => {
    respond({
      templates: () => problem(422, 'ECR-INT-0422', 'err.ECR-INT-0422.eventQueryNotConfigured'),
    });
    show();

    await waitFor(() => expect(document.querySelector('[data-testid="source-events-not-configured"]')).not.toBeNull());
    expect(screen.queryByRole('alert')).toBeNull();
    await firstRow();
  });

  it('«Отримати з PI зараз» — POST sync; без мапінгу (422 eventSyncNoMap) — підказка і дія «Створити мапінг»', async () => {
    respond({ sync: () => problem(422, 'ECR-INT-0422', 'err.ECR-INT-0422.eventSyncNoMap', { sourceEntityId: 42 }) });
    show();
    await firstRow();

    fireEvent.click(screen.getByRole('button', { name: '⟦sourceEvents.syncNow⟧' }));

    await waitFor(() => expect(document.querySelector('[data-source-events-sync-create-map]')).not.toBeNull());
    expect(sent.some((s) => s.method === 'POST' && s.path === '/api/v1/sources/42/source-events/sync')).toBe(true);
    expect(screen.getByText('⟦sourceEvents.syncNoMapHint⟧')).toBeTruthy();
  });

  it('пауза мапінгу — PUT з тими самими полями й isActive: false', async () => {
    respond();
    show();

    fireEvent.click(await waitFor(() => {
      const button = document.querySelector<HTMLElement>('[data-source-event-map-toggle="12"]');
      expect(button).not.toBeNull();
      return button as HTMLElement;
    }));

    await waitFor(() => expect(sent.some((s) => s.method === 'PUT')).toBe(true));
    const put = sent.find((s) => s.method === 'PUT');
    expect(put?.path).toBe('/api/v1/source-event-maps/12');
    expect(put?.body).toMatchObject({ isActive: false, volumeMode: 'None', filterAttribute: null });
    expect((put?.body as { fields: unknown[] }).fields).toHaveLength(2);
  });

  it('видалення мапінгу зі зв\'язками (409 eventMapHasLinks) — причина сервера в панелі, мапінг лишається', async () => {
    respond({ remove: () => problem(409, 'ECR-INT-0409', 'err.ECR-INT-0409.eventMapHasLinks', { links: 3 }) });
    show();

    fireEvent.click(await waitFor(() => {
      const button = document.querySelector<HTMLElement>('[data-source-event-map-delete="12"]');
      expect(button).not.toBeNull();
      return button as HTMLElement;
    }));
    const confirm = await screen.findByRole('dialog');
    fireEvent.click(within(confirm).getByRole('button', { name: '⟦sourceEvents.mapDelete⟧' }));

    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy());
    expect(sent.some((s) => s.method === 'DELETE' && s.path === '/api/v1/source-event-maps/12')).toBe(true);
    expect(document.querySelector('[data-source-event-map="12"]')).not.toBeNull();
  });

  const firstPageReads = (): number =>
    sent.filter((s) => s.method === 'GET' && s.path === '/api/v1/sources/42/source-events' && !s.search.includes('cursor')).length;

  it('успішний «Отримати з PI зараз» — список подій перечитується (інвалідація ключа подій)', async () => {
    respond();
    show();
    await firstRow();
    expect(firstPageReads()).toBe(1);

    fireEvent.click(screen.getByRole('button', { name: '⟦sourceEvents.syncNow⟧' }));

    await waitFor(() => expect(firstPageReads()).toBe(2));
  });

  it('пауза мапінгу — список подій перечитується разом із мапінгами', async () => {
    respond();
    show();
    await firstRow();
    expect(firstPageReads()).toBe(1);

    fireEvent.click(await waitFor(() => {
      const button = document.querySelector<HTMLElement>('[data-source-event-map-toggle="12"]');
      expect(button).not.toBeNull();
      return button as HTMLElement;
    }));

    await waitFor(() => expect(firstPageReads()).toBe(2));
  });

  it('UTC-час — видимим текстом рядка, а не лише в title', async () => {
    respond();
    show();

    const row = await firstRow();

    expect(row.querySelector('[data-event-start-utc]')?.textContent).toMatch(/4:00:00.*UTC/);
    expect(row.querySelector('[data-event-end-utc]')?.textContent).toMatch(/4:15:00.*UTC/);
  });

  it('без Integration.Manage — ні створення, ні зміни, ні паузи, ні «Отримати з PI зараз»', async () => {
    respond();
    show(false);
    await firstRow();

    await waitFor(() => expect(document.querySelector('[data-source-event-map="12"]')).not.toBeNull());
    expect(document.querySelector('[data-source-event-map-create]')).toBeNull();
    expect(document.querySelector('[data-source-event-map-edit]')).toBeNull();
    expect(document.querySelector('[data-source-event-map-toggle]')).toBeNull();
    expect(document.querySelector('[data-source-event-map-delete]')).toBeNull();
    expect(document.querySelector('[data-source-events-sync]')).toBeNull();
  });
});

describe('eventsQuery', () => {
  it('фільтри → рядок запиту: стани повторюваним параметром, діапазон дат — межі доби UTC, «до» виключно', () => {
    expect(
      eventsQuery({ mapId: 12, statuses: ['Missing', 'Open'], periodKey: 202609, from: '2026-09-01', to: '2026-09-30' }),
    ).toEqual({
      mapId: 12,
      status: ['Missing', 'Open'],
      periodKey: 202609,
      fromUtc: '2026-09-01T00:00:00.000Z',
      toUtc: '2026-10-01T00:00:00.000Z',
      limit: 50,
    });
    expect(eventsQuery({ mapId: null, statuses: [], periodKey: null, from: null, to: null })).toEqual({ limit: 50 });
  });
});

describe('formatInZone', () => {
  it('час показується в поясі проєкту, а не браузера; невідомий пояс — сирий локальний рядок сервера', () => {
    expect(formatInZone('2026-09-30T04:00:00Z', 'Asia/Atyrau', null)).toMatch(/9:00:00/);
    expect(formatInZone('2026-09-30T04:00:00Z', 'Not/AZone', '2026-09-30T09:00:00+05:00')).toBe('2026-09-30T09:00:00+05:00');
    expect(formatInZone(null, 'Asia/Atyrau', null)).toBe('');
  });
});

/**
 * Клавіатура вкладки (WCAG 2.4.3, 2.4.6).
 *
 * ⛔ Мутаційні докази (перевірено руками 2026-09-30): прибрати `useReturnFocusOnUnmount()` у
 * `SourceEventMapModal` → червоний «повернення фокуса»; прибрати `aria-label` у кнопок рядка мапінгу →
 * червоний «ім'я з документом»; прибрати `toggleFocus.arm(...)` → червоний «пауза».
 */
describe('SourceEventsTab — клавіатура', () => {
  const editButton = (): Promise<HTMLElement> =>
    waitFor(() => {
      const button = document.querySelector<HTMLElement>('[data-source-event-map-edit="12"]');
      expect(button).not.toBeNull();
      return button as HTMLElement;
    });

  it('повернення фокуса: «Скасувати» у формі мапінгу — фокус назад на «Змінити»', async () => {
    respond();
    show();

    const edit = await editButton();
    edit.focus();
    fireEvent.click(edit);

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦common.cancel⟧' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(edit));
  });

  it('ім\'я з документом: кнопки рядка мапінгу розрізняються', async () => {
    respond();
    show();

    const edit = await editButton();
    expect(edit.getAttribute('aria-label')).toMatch(/^⟦sourceEvents\.mapEdit⟧: .*DOC-000123/);
    expect(document.querySelector('[data-source-event-map-toggle="12"]')?.getAttribute('aria-label')).toMatch(
      /^⟦sourceEvents\.mapPause⟧: .*DOC-000123/,
    );
  });

  it('пауза: після відповіді фокус назад на натиснуту кнопку, а не на <body>', async () => {
    respond();
    // ⚠ Відповідь не миттєва: миттєву React Query зводить в один рендер, і стану «запит іде» не буває.
    const answer = globalThis.fetch;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        if (init?.method === 'PUT') await new Promise((resolve) => setTimeout(resolve, 50));
        return answer(input, init);
      }),
    );
    show();

    const toggle = await waitFor(() => {
      const button = document.querySelector<HTMLElement>('[data-source-event-map-toggle="12"]');
      expect(button).not.toBeNull();
      return button as HTMLElement;
    });
    toggle.focus();
    fireEvent.click(toggle);
    // Браузер знімає фокус із кнопки, що стала `loading` (= `disabled`); jsdom — ні.
    toggle.blur();

    await waitFor(() => expect(sent.some((s) => s.method === 'PUT')).toBe(true));
    await waitFor(() => expect(document.activeElement).toBe(toggle));
  });
});
