import { describe, it, expect, vi, afterEach, beforeAll } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { createQueryClient } from '@/app/queryClient';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * Вкладка «Entities» шухляди з'єднання: сутність збору з каталогу
 * (`ФВ-13.11`) і прив'язка до довідника (`ФВ-8.11`).
 *
 * ⛔ Запити ловляться на рівні `fetch` разом із методом і тілом: доказ у тому,
 * що на сервер пішли саме код і шлях ОБРАНОЇ позиції каталогу, а не що форма
 * щось показала.
 */
function connection(): Record<string, unknown> {
  return {
    catalog: null,
    code: 'PI-MAIN',
    collectionSchedules: 0,
    endpoint: 'https://pi-main.example.invalid/api',
    hasSecret: false,
    id: 7,
    isActive: true,
    maxParallel: 4,
    nameL10n: { en: 'Main PI server' },
    rowVersion: 'AAAAAAAAB9E=',
    secondaryEndpoint: null,
    sourceEntities: 1,
    transport: 'PiWebApi',
  };
}

function entity(id: number, code: string, dataSourceId: number, registryDefId: number | null): Record<string, unknown> {
  return {
    code,
    dataSourceCode: dataSourceId === 7 ? 'PI-MAIN' : 'LAB',
    dataSourceId,
    displayName: `${code} entity`,
    entityPath: null,
    id,
    isActive: true,
    lastRun: null,
    oldestGap: null,
    registryDefId,
    transport: 'PiWebApi',
  };
}

const Registries = [
  { id: 70, code: 'PERMITS', nameL10n: { values: { en: 'Permits' } }, isTemporal: false },
  { id: 71, code: 'STACKS', nameL10n: { values: { en: 'Stacks' } }, isTemporal: false },
];

/** Каталог за шляхом рівня; `''` — корінь. */
const Catalog: Record<string, Record<string, unknown>[]> = {
  '': [{ code: 'Plant', displayName: 'Plant', path: '\\\\AF\\ECR\\Plant', kind: 'Element', dataType: 'Element', unitSymbol: null }],
  '\\\\AF\\ECR\\Plant': [
    { code: 'Flare_01', displayName: 'Flare 01', path: '\\\\AF\\ECR\\Plant\\Flare_01', kind: 'Element', dataType: 'Element', unitSymbol: null },
  ],
};

interface Sent {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
}

let sent: Sent[] = [];
let catalogCalls = 0;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function respond({
  permissions = ['Integration.View', 'Integration.Manage'],
  create = () => json({ id: 50 }, 201),
  catalogOutage = false,
}: {
  permissions?: string[];
  create?: () => Response;
  catalogOutage?: boolean;
} = {}): void {
  sent = [];
  catalogCalls = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;
      const method = init?.method ?? 'GET';

      if (method !== 'GET') {
        sent.push({ method, path, body: JSON.parse(String(init?.body ?? 'null')) });
      }

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path === '/api/v1/data-sources') return json([connection()]);
      if (path === '/api/v1/data-sources/7/catalog') {
        catalogCalls += 1;
        if (catalogOutage) {
          return json(
            { title: 'Source unavailable', status: 503, errorCode: 'ECR-INT-0503', correlationId: 'c-503' },
            503,
          );
        }
        return json({ items: Catalog[url.searchParams.get('path') ?? ''] ?? [], nextCursor: null });
      }
      if (path === '/api/v1/registries') return json(Registries);
      if (path === '/api/v1/sources' && method === 'POST') return create();
      if (path === '/api/v1/sources') return json([entity(42, 'STACK-1', 7, 70), entity(43, 'LAB-1', 8, null)]);
      if (/^\/api\/v1\/sources\/\d+\/registry$/.test(path)) {
        const body = JSON.parse(String(init?.body)) as { registryDefId: number | null };
        return json({ ...entity(42, 'STACK-1', 7, body.registryDefId), sourceKind: 'External' });
      }

      return json(null);
    }),
  );
}

function show(client: QueryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })): void {
  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/sources?panel=PI-MAIN']}>
          <SourcesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openEntitiesTab(): Promise<HTMLElement> {
  const drawer = await screen.findByRole('dialog');
  fireEvent.click(await within(drawer).findByRole('tab', { name: /sources\.tabEntities/ }));

  return drawer;
}

/** Прогрів лінивих чанків — той самий прийом, що в `DataSourceScheduleTab.test.tsx`. */
beforeAll(async () => {
  await import('@/features/integration/SourceEntitiesTab');
  await import('@/features/integration/AddSourceEntityModal');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Шухляда з'єднання: вкладка Entities", () => {
  it('ФВ-13.11: без Integration.Manage вкладки немає', async () => {
    respond({ permissions: ['Integration.View'] });
    show();

    const drawer = await screen.findByRole('dialog');
    await within(drawer).findByRole('tab', { name: /sources\.tabSchedule/ });

    expect(within(drawer).queryByRole('tab', { name: /sources\.tabEntities/ })).toBeNull();
  });

  it('ФВ-13.11: показує лише сутності цього з\'єднання', async () => {
    respond();
    show();

    const drawer = await openEntitiesTab();

    await waitFor(() => expect(drawer.querySelector('[data-entity-row="STACK-1"]')).not.toBeNull());
    expect(drawer.querySelector('[data-entity-row="LAB-1"]')).toBeNull();
  });

  it('D11: недоступний каталог (503) не повторюється — причина видна з першої відповіді, а не через ~3 спроби', async () => {
    respond({ catalogOutage: true });
    // Реальні типові опції застосунку: глобальне правило повторює 5xx двічі з паузою.
    show(createQueryClient());

    const drawer = await openEntitiesTab();
    fireEvent.click(await within(drawer).findByRole('button', { name: /sources\.addEntity/ }));

    const form = await screen.findByRole('dialog', { name: /sources\.addEntityTitle/ });

    // findByRole чекає ~1 с; із повторами (паузи 1 с + 2 с) причина з'явилась би щонайменше за 3 с.
    await within(form).findByRole('alert');
    expect(catalogCalls).toBe(1);
  });

  it('ФВ-13.11: сутність обирається з каталогу і заводиться POST /api/v1/sources з кодом і шляхом позиції', async () => {
    respond();
    show();

    const drawer = await openEntitiesTab();
    fireEvent.click(await within(drawer).findByRole('button', { name: /sources\.addEntity/ }));

    const form = await screen.findByRole('dialog', { name: /sources\.addEntityTitle/ });
    const save = within(form).getByRole('button', { name: /sources\.addEntity/ }) as HTMLButtonElement;

    // ⛔ Без вибору з каталогу зберегти нічого: код руками не вводиться.
    expect(save.disabled).toBe(true);

    const plant = await waitFor(() => {
      const row = form.querySelector<HTMLElement>('[data-catalog-item="Plant"]');
      expect(row).not.toBeNull();
      return row as HTMLElement;
    });
    fireEvent.click(within(plant).getByRole('button', { name: /sources\.catalogOpenLevel/ }));

    const flare = await waitFor(() => {
      const row = form.querySelector<HTMLElement>('[data-catalog-item="Flare_01"]');
      expect(row).not.toBeNull();
      return row as HTMLElement;
    });
    fireEvent.click(within(flare).getByRole('button', { name: /sources\.catalogChoose/ }));

    await waitFor(() => expect(save.disabled).toBe(false));
    fireEvent.click(save);

    await waitFor(() => expect(sent.some((call) => call.method === 'POST')).toBe(true));
    expect(sent).toEqual([
      {
        method: 'POST',
        path: '/api/v1/sources',
        body: {
          dataSourceId: 7,
          code: 'Flare_01',
          displayName: 'Flare 01',
          entityPath: '\\\\AF\\ECR\\Plant\\Flare_01',
          sourceKind: null,
        },
      },
    ]);
  });

  it('ФВ-13.11: дубль коду — відмова 409 видна у формі, форма лишається відкритою', async () => {
    respond({
      create: () =>
        json(
          {
            title: 'Conflict',
            status: 409,
            errorCode: 'ECR-INT-0409',
            messageKey: 'err.ECR-INT-0409.sourceEntityDuplicate',
            detail: 'Connection "PI-MAIN" already has a collection entity with code "Plant".',
            correlationId: 'c-409',
          },
          409,
        ),
    });
    show();

    const drawer = await openEntitiesTab();
    fireEvent.click(await within(drawer).findByRole('button', { name: /sources\.addEntity/ }));

    const form = await screen.findByRole('dialog', { name: /sources\.addEntityTitle/ });
    const plant = await waitFor(() => {
      const row = form.querySelector<HTMLElement>('[data-catalog-item="Plant"]');
      expect(row).not.toBeNull();
      return row as HTMLElement;
    });
    fireEvent.click(within(plant).getByRole('button', { name: /sources\.catalogChoose/ }));
    fireEvent.click(within(form).getByRole('button', { name: /sources\.addEntity/ }));

    expect(await within(form).findByRole('alert')).toBeTruthy();
    expect(screen.queryByRole('dialog', { name: /sources\.addEntityTitle/ })).not.toBeNull();
  });

  it('ФВ-8.11: поточна прив\'язка видна, вибір довідника шле PUT з id, «Not bound» — з null', async () => {
    respond();
    show();

    const drawer = await openEntitiesTab();

    const select = (await within(drawer).findByRole('textbox', { name: /sources\.registry/ })) as HTMLInputElement;
    await waitFor(() => expect(select.value).toBe('Permits'));
    await waitFor(() => expect(select.disabled).toBe(false));

    fireEvent.click(select);
    fireEvent.click(await screen.findByRole('option', { name: 'Stacks' }));

    await waitFor(() => expect(sent).toHaveLength(1));

    fireEvent.click(select);
    fireEvent.click(await screen.findByRole('option', { name: /sources\.registryNone/ }));

    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent).toEqual([
      { method: 'PUT', path: '/api/v1/sources/42/registry', body: { registryDefId: 71 } },
      { method: 'PUT', path: '/api/v1/sources/42/registry', body: { registryDefId: null } },
    ]);
  });
});
