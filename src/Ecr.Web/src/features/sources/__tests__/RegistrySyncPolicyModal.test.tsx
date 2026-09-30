import { describe, it, expect, vi, afterEach, beforeAll } from 'vitest';
import { cleanup, render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * Політика синку довідника з AF (`D-212` PR-8): кнопка у вкладці Entities
 * шухляди з'єднання → форма → `PUT /api/v1/sources/{id}/registry/policy`.
 *
 * ⛔ Запити ловляться на рівні `fetch` з методом і тілом: доказ у тому, що
 * на сервер пішла саме обрана політика, а не що форма щось показала.
 */

interface Sent {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
}

let sent: Sent[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const Connection = {
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
  sourceEntities: 2,
  transport: 'PiSqlClient',
};

/** Рядок переліку `GET /api/v1/sources` — БЕЗ полів політики, як віддає сервер. */
function row(id: number, code: string, registryDefId: number | null): Record<string, unknown> {
  return {
    code,
    dataSourceCode: 'PI-MAIN',
    dataSourceId: 7,
    displayName: `${code} entity`,
    entityPath: null,
    id,
    isActive: true,
    lastRun: null,
    oldestGap: null,
    registryDefId,
    transport: 'PiSqlClient',
  };
}

/** `SourceEntityDto` — відповідь на прив'язку й на збереження політики. */
function dto(id: number, registryDefId: number | null, policy: Record<string, unknown>): Record<string, unknown> {
  return {
    id,
    dataSourceId: 7,
    code: 'STACK-1',
    displayName: 'STACK-1 entity',
    entityPath: null,
    sourceKind: 'External',
    registryDefId,
    isActive: true,
    ...policy,
  };
}

const Registries = [
  { id: 70, code: 'PERMITS', nameL10n: { values: { en: 'Permits' } }, isTemporal: true },
  { id: 71, code: 'STACKS', nameL10n: { values: { en: 'Stacks' } }, isTemporal: true },
];

const Stored = {
  onMissingInSource: 'Deactivate',
  validFromAttribute: 'StartDate',
  validToAttribute: 'EndDate',
  validToInclusive: true,
};

function respond({
  permissions = ['Integration.View', 'Integration.Manage', 'Registry.EditData'],
  grants = {},
  policy = (body: unknown) => json(dto(42, 70, body as Record<string, unknown>)),
}: {
  permissions?: string[];
  grants?: Record<string, string>;
  policy?: (body: unknown) => Response;
} = {}): void {
  sent = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;
      const method = init?.method ?? 'GET';
      const body: unknown = method === 'GET' ? undefined : JSON.parse(String(init?.body ?? 'null'));

      if (method !== 'GET') sent.push({ method, path, body });

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants,
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path === '/api/v1/data-sources') return json([Connection]);
      if (path === '/api/v1/registries') return json(Registries);
      if (path === '/api/v1/sources') return json([row(42, 'STACK-1', 70), row(44, 'LOOSE-1', null)]);
      if (path === '/api/v1/sources/42/registry/policy') return policy(body);
      if (path === '/api/v1/sources/42/registry') {
        return json(dto(42, (body as { registryDefId: number }).registryDefId, Stored));
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

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
  await waitFor(() => expect(drawer.querySelector('[data-entity-row="STACK-1"]')).not.toBeNull());

  return drawer;
}

/** Кнопка політики сутності `code` або `null`, коли її немає. */
function policyButton(drawer: HTMLElement, code: string): HTMLElement | null {
  return drawer.querySelector<HTMLElement>(`[data-entity-sync-policy="${code}"]`);
}

async function openPolicy(drawer: HTMLElement): Promise<HTMLElement> {
  const button = await waitFor(() => {
    const found = policyButton(drawer, 'STACK-1');
    expect(found).not.toBeNull();
    return found as HTMLElement;
  });
  fireEvent.click(button);

  return screen.findByRole('dialog', { name: /sources\.syncPolicyTitle/ });
}

function input(form: HTMLElement, selector: string): HTMLInputElement {
  const found = form.querySelector<HTMLInputElement>(selector);
  expect(found).not.toBeNull();
  return found as HTMLInputElement;
}

beforeAll(async () => {
  await import('@/features/integration/SourceEntitiesTab');
  await import('@/features/sources/RegistrySyncPolicyModal');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('D-212: політика синку довідника з AF', () => {
  it('кнопки немає без права на дані довідника; є з грантом Write саме на цей довідник', async () => {
    respond({ permissions: ['Integration.View', 'Integration.Manage'], grants: { 'Registry:71': 'Write' } });
    show();

    let drawer = await openEntitiesTab();
    // ⛔ Грант на ІНШИЙ довідник не дає права на політику прив'язаного.
    expect(policyButton(drawer, 'STACK-1')).toBeNull();

    cleanup();
    vi.unstubAllGlobals();
    respond({ permissions: ['Integration.View', 'Integration.Manage'], grants: { 'Registry:70': 'Write' } });
    show();

    drawer = await openEntitiesTab();
    await waitFor(() => expect(policyButton(drawer, 'STACK-1')).not.toBeNull());
    // Неприв'язаній сутності політика не має до чого застосуватися.
    expect(policyButton(drawer, 'LOOSE-1')).toBeNull();
  });

  it('грант Read на довідник — кнопки немає', async () => {
    respond({ permissions: ['Integration.View', 'Integration.Manage'], grants: { 'Registry:70': 'Read' } });
    show();

    const drawer = await openEntitiesTab();
    await waitFor(() => expect(drawer.querySelector('[data-entity-registry="STACK-1"]')).not.toBeNull());
    expect(policyButton(drawer, 'STACK-1')).toBeNull();
  });

  it('чинна політика невідома — типова з попередженням; збереження шле PUT з обраним тілом', async () => {
    respond();
    show();

    const form = await openPolicy(await openEntitiesTab());

    expect(form.querySelector('[data-sync-policy-unknown]')).not.toBeNull();
    expect(input(form, 'input[value="MarkOrphaned"]').checked).toBe(true);

    fireEvent.click(input(form, 'input[value="Ignore"]'));
    fireEvent.change(input(form, 'input[data-sync-policy-valid-from]'), { target: { value: '  Start ' } });
    fireEvent.change(input(form, 'input[data-sync-policy-valid-to]'), { target: { value: 'End' } });
    fireEvent.click(input(form, 'input[type="checkbox"]'));
    fireEvent.click(within(form).getByRole('button', { name: /common\.save/ }));

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]).toEqual({
      method: 'PUT',
      path: '/api/v1/sources/42/registry/policy',
      body: { onMissingInSource: 'Ignore', validFromAttribute: 'Start', validToAttribute: 'End', validToInclusive: true },
    });
    await waitFor(() => expect(screen.queryByRole('dialog', { name: /sources\.syncPolicyTitle/ })).toBeNull());
  });

  it('форма показує політику з SourceEntityDto (відповідь прив\'язки); порожній атрибут — null, «включно» без кінця — false', async () => {
    respond();
    show();

    const drawer = await openEntitiesTab();
    // Перший рядок — STACK-1 (порядок переліку).
    const [select] = (await within(drawer).findAllByRole('textbox', { name: /sources\.registry/ })) as HTMLInputElement[];
    if (select === undefined) throw new Error('немає вибору довідника');
    await waitFor(() => expect(select.disabled).toBe(false));
    fireEvent.click(select);
    fireEvent.click(await screen.findByRole('option', { name: 'Stacks' }));
    await waitFor(() => expect(sent).toHaveLength(1));

    const form = await openPolicy(drawer);

    expect(form.querySelector('[data-sync-policy-unknown]')).toBeNull();
    expect(input(form, 'input[value="Deactivate"]').checked).toBe(true);
    expect(input(form, 'input[data-sync-policy-valid-from]').value).toBe('StartDate');
    expect(input(form, 'input[data-sync-policy-valid-to]').value).toBe('EndDate');
    expect(input(form, 'input[type="checkbox"]').checked).toBe(true);

    fireEvent.change(input(form, 'input[data-sync-policy-valid-from]'), { target: { value: '   ' } });
    fireEvent.change(input(form, 'input[data-sync-policy-valid-to]'), { target: { value: '' } });
    fireEvent.click(within(form).getByRole('button', { name: /common\.save/ }));

    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]?.body).toEqual({
      onMissingInSource: 'Deactivate',
      validFromAttribute: null,
      validToAttribute: null,
      validToInclusive: false,
    });
  });

  it.each([
    [422, 'ECR-REQ-0422', 'err.ECR-REQ-0422.registrySyncPolicyInvalid', 'Sync policy: an attribute name is too long.'],
    [403, 'ECR-AUTH-0403', 'err.ECR-AUTH-0403.permission', 'Permission Registry.EditData is required.'],
  ])('%i — причина сервера (за messageKey) у формі, форма лишається відкритою', async (status, code, key, text) => {
    respond({
      policy: () =>
        json(
          { title: 'Refused', status, errorCode: code, messageKey: key, detail: text, correlationId: 'c-1' },
          status,
        ),
    });
    show();

    const form = await openPolicy(await openEntitiesTab());
    fireEvent.click(within(form).getByRole('button', { name: /common\.save/ }));

    // ⚠ Попередження «чинна політика невідома» — теж `alert`; шукаємо саме відмову.
    await waitFor(() =>
      expect(within(form).getAllByRole('alert').some((alert) => alert.textContent?.includes(text))).toBe(true),
    );
    expect(screen.queryByRole('dialog', { name: /sources\.syncPolicyTitle/ })).not.toBeNull();
  });
});
