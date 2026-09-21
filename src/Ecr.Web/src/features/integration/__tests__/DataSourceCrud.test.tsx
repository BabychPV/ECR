import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { SourcesPage } from '@/pages/admin/SourcesPage';
import { language } from '@/shared/i18n';

/**
 * Створення, правка й видалення з'єднань на `/admin/sources` (`UI-09`, крок 2).
 *
 * ⛔ Рендериться ВСЯ сторінка, а запити ловляться на рівні `fetch`: доказ у
 * тому, що на сервер пішов саме той метод, шлях і тіло, а не що компонент
 * викликав якусь функцію.
 */
const Connection = {
  catalog: 'ProdAF',
  code: 'PI-MAIN',
  collectionSchedules: 0,
  endpoint: 'https://pi.example.invalid/piwebapi',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server', ru: 'Основной сервер PI' },
  secondaryEndpoint: null,
  sourceEntities: 0,
  transport: 'PiWebApi',
};

interface Call {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
  readonly headers: Headers;
}

let calls: Call[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/**
 * Профіль, перелік і відповідь на ЗМІНУ (`POST`/`PUT`/`DELETE`) — за вибором тесту.
 */
function respond({
  permissions = ['Integration.View', 'Integration.Manage'],
  change = () => json(Connection),
}: {
  permissions?: string[];
  change?: (call: Call) => Response;
} = {}): void {
  calls = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), 'http://localhost').pathname;
      const method = init?.method ?? 'GET';
      const call: Call = {
        method,
        path,
        body: init?.body === undefined ? undefined : JSON.parse(String(init.body)),
        headers: new Headers(init?.headers),
      };

      calls.push(call);

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

      if (method !== 'GET') return change(call);

      if (path.endsWith('/api/v1/data-sources')) return json([Connection]);
      if (path.endsWith('/api/v1/sources')) return json([]);

      return json(null);
    }),
  );
}

function Location(): JSX.Element {
  return <output data-testid="location">{useLocation().search}</output>;
}

function show(entry = '/admin/sources'): void {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <SourcesPage />
          <Location />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function listReads(): number {
  return calls.filter((call) => call.method === 'GET' && call.path.endsWith('/api/v1/data-sources'))
    .length;
}

function changes(): Call[] {
  return calls.filter((call) => call.method !== 'GET');
}

async function form(): Promise<HTMLElement> {
  return waitFor(() => {
    const node = document.querySelector<HTMLElement>('[data-data-source-form]');
    expect(node, 'форма з\'єднання').not.toBeNull();

    return node as HTMLElement;
  });
}

function field(scope: HTMLElement, label: RegExp): HTMLInputElement {
  return within(scope).getByLabelText(label) as HTMLInputElement;
}

function type(scope: HTMLElement, label: RegExp, value: string): void {
  fireEvent.change(field(scope, label), { target: { value } });
}

/** Текст помилки, прив'язаної до поля (`aria-describedby`). */
function errorOf(input: HTMLInputElement): string {
  const ids = (input.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);

  return ids.map((id) => document.getElementById(id)?.textContent ?? '').join(' ');
}

async function openCreate(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByRole('button', { name: /sources\.newConnection/ }));

  return form();
}

async function openEdit(): Promise<HTMLElement> {
  const drawer = await screen.findByRole('dialog');
  fireEvent.click(await within(drawer).findByRole('button', { name: /sources\.editConnection/ }));

  return form();
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("З'єднання: створення", () => {
  it('кнопки «нове з\'єднання» немає без Integration.Manage', async () => {
    respond({ permissions: ['Integration.View'] });
    show();

    await screen.findByText('Main PI server');

    expect(document.querySelector('[data-new-connection]')).toBeNull();
  });

  it('дзеркало: валідні дані → POST із тілом форми; поля секрету немає; перелік перечитано', async () => {
    respond({ change: () => json({ ...Connection, id: 9, code: 'LAB' }, 201) });
    show();

    await screen.findByText('Main PI server');
    const scope = await openCreate();

    // ⛔ Q15-06: службовий обліковий запис — поля секрету немає зовсім.
    expect(scope.querySelector('input[type="password"]')).toBeNull();

    type(scope, /sources\.code/, 'LAB');
    type(scope, /sources\.name/, 'Lab feed');
    type(scope, /^.*sources\.endpoint/, 'Server=lab;Database=Lims');
    type(scope, /sources\.catalog/, 'Lims');

    const before = listReads();
    fireEvent.click(within(scope).getByRole('button', { name: /sources\.create/ }));

    await waitFor(() => expect(changes()).toHaveLength(1));

    const [post] = changes();
    expect(post?.method).toBe('POST');
    expect(post?.path).toBe('/api/v1/data-sources');
    expect(post?.body).toEqual({
      code: 'LAB',
      nameL10n: { [language()]: 'Lab feed' },
      transport: 'PiWebApi',
      endpoint: 'Server=lab;Database=Lims',
      secondaryEndpoint: null,
      catalog: 'Lims',
      maxParallel: null,
      isActive: true,
    });

    await waitFor(() => expect(listReads()).toBeGreaterThan(before));
    await waitFor(() => expect(document.querySelector('[data-data-source-form]')).toBeNull());
  });

  it('422 dataSourceEndpointCarriesSecret — причина БІЛЯ поля адреси, чернетка ціла', async () => {
    respond({
      change: () =>
        json(
          {
            title: 'Invalid request',
            status: 422,
            errorCode: 'ECR-REQ-0422',
            correlationId: 'c-422',
            messageKey: 'err.ECR-REQ-0422.dataSourceEndpointCarriesSecret',
            code: null,
          },
          422,
        ),
    });
    show();

    await screen.findByText('Main PI server');
    const scope = await openCreate();

    type(scope, /sources\.code/, 'LAB');
    type(scope, /sources\.name/, 'Lab feed');
    type(scope, /^.*sources\.endpoint/, 'https://user:pass@lab.example.invalid/api');

    fireEvent.click(within(scope).getByRole('button', { name: /sources\.create/ }));

    const endpoint = field(scope, /^.*sources\.endpoint/);

    await waitFor(() =>
      expect(errorOf(endpoint)).toContain('err.ECR-REQ-0422.dataSourceEndpointCarriesSecret'),
    );
    expect(endpoint.getAttribute('aria-invalid')).toBe('true');

    // Не загальний банер, а саме поле.
    expect(within(scope).queryByRole('alert')).toBeNull();

    // Введене не загублене.
    expect(endpoint.value).toBe('https://user:pass@lab.example.invalid/api');
    expect(field(scope, /sources\.name/).value).toBe('Lab feed');
  });
});

describe("З'єднання: правка", () => {
  it('кнопки правки й видалення немає без Integration.Manage', async () => {
    respond({ permissions: ['Integration.View'] });
    show('/admin/sources?panel=PI-MAIN');

    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('ProdAF')).toBeTruthy();

    expect(drawer.querySelector('[data-edit-connection]')).toBeNull();
    expect(drawer.querySelector('[data-delete-connection]')).toBeNull();
  });

  it('дзеркало: PUT на id рядка; назва мовою інтерфейсу поверх інших мов; код не змінюється', async () => {
    respond();
    show('/admin/sources?panel=PI-MAIN');

    const scope = await openEdit();

    // Чернетка — з рядка, який бачить людина.
    expect(field(scope, /^.*sources\.endpoint/).value).toBe(Connection.endpoint);
    expect(field(scope, /sources\.code/).disabled).toBe(true);

    type(scope, /sources\.name/, 'Main PI (new)');
    fireEvent.click(within(scope).getByRole('switch', { name: /sources\.isActive/ }));

    const before = listReads();
    fireEvent.click(within(scope).getByRole('button', { name: /common\.save/ }));

    await waitFor(() => expect(changes()).toHaveLength(1));

    const [put] = changes();
    expect(put?.method).toBe('PUT');
    expect(put?.path).toBe('/api/v1/data-sources/7');
    expect(put?.body).toEqual({
      code: 'PI-MAIN',
      nameL10n: { ...Connection.nameL10n, [language()]: 'Main PI (new)' },
      transport: 'PiWebApi',
      endpoint: Connection.endpoint,
      secondaryEndpoint: null,
      catalog: 'ProdAF',
      maxParallel: 4,
      isActive: false,
    });

    // ⚠ Версії рядка в контракті з'єднання немає (`DataSourceView`), тож і
    // `If-Match` не шлеться — тест фіксує це, щоб поява версії не пройшла тихо.
    expect(put?.headers.has('If-Match')).toBe(false);

    await waitFor(() => expect(listReads()).toBeGreaterThan(before));
  });

  it('відмова правки не губить чернетку й показує причину', async () => {
    respond({
      change: () =>
        json(
          {
            title: 'Invalid request',
            status: 422,
            errorCode: 'ECR-REQ-0422',
            correlationId: 'c-422',
            detail: 'A data source needs a name in at least one language.',
            messageKey: 'err.ECR-REQ-0422.dataSourceInvalid',
          },
          422,
        ),
    });
    show('/admin/sources?panel=PI-MAIN');

    const scope = await openEdit();

    type(scope, /sources\.name/, 'Main PI (draft)');
    type(scope, /sources\.catalog/, 'OtherAF');
    fireEvent.click(within(scope).getByRole('button', { name: /common\.save/ }));

    const alert = await within(scope).findByRole('alert');
    expect(alert.textContent).toContain('A data source needs a name');

    expect(field(scope, /sources\.name/).value).toBe('Main PI (draft)');
    expect(field(scope, /sources\.catalog/).value).toBe('OtherAF');
  });
});

describe("З'єднання: видалення", () => {
  it('L6: спершу підтвердження з фокусом на Cancel; DELETE — лише після дієслова', async () => {
    respond({ change: () => new Response(null, { status: 204 }) });
    show('/admin/sources?panel=PI-MAIN');

    const drawer = await screen.findByRole('dialog');
    fireEvent.click(await within(drawer).findByRole('button', { name: /sources\.deleteConnection/ }));

    const cancel = await screen.findByTestId('confirm-cancel');
    await waitFor(() => expect(document.activeElement).toBe(cancel));
    expect(changes()).toHaveLength(0);

    const before = listReads();
    fireEvent.click(screen.getByTestId('confirm-verb'));

    await waitFor(() => expect(changes()).toHaveLength(1));
    expect(changes()[0]?.method).toBe('DELETE');
    expect(changes()[0]?.path).toBe('/api/v1/data-sources/7');

    await waitFor(() => expect(listReads()).toBeGreaterThan(before));
    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe(''));
  });

  it('409 dataSourceInUse — причина в шухляді, а не «не вдалося»', async () => {
    respond({
      change: () =>
        json(
          {
            title: 'Conflicting state',
            status: 409,
            errorCode: 'ECR-JOB-0409',
            correlationId: 'c-409',
            detail: 'This data source still carries 12 collection entities and 3 schedules.',
            messageKey: 'err.ECR-JOB-0409.dataSourceInUse',
            sourceEntities: '12',
            collectionSchedules: '3',
          },
          409,
        ),
    });
    show('/admin/sources?panel=PI-MAIN');

    const drawer = await screen.findByRole('dialog');
    fireEvent.click(await within(drawer).findByRole('button', { name: /sources\.deleteConnection/ }));
    fireEvent.click(await screen.findByTestId('confirm-verb'));

    const failure = await waitFor(() => {
      const node = drawer.querySelector<HTMLElement>('[data-delete-failure]');
      expect(node).not.toBeNull();

      return node as HTMLElement;
    });

    expect(failure.textContent).toContain('12 collection entities and 3 schedules');
    expect(failure.textContent).toContain('ECR-JOB-0409');

    // Шухляда лишилася відкритою: з'єднання нікуди не ділося.
    expect(screen.getByTestId('location').textContent).toBe('?panel=PI-MAIN');
  });
});
