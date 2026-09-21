import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * `/admin/sources` лише з `Integration.View` (сервер `0e73e80e`): з'єднання
 * видно, дій немає, а перелік СУТНОСТЕЙ збору (`/api/v1/sources`, лише
 * `Manage`) відповідає `403` — і сторінка має це сказати, а не показати
 * «сутностей немає» (`L10`: «немає прав» ≠ «порожньо»).
 *
 * ⚠ Сервер тут відтворено відповідями, а не перевіркою прав у клієнті: сама
 * сторінка прав на перелік не перевіряє, вона показує те, що відповів сервер.
 */
const Connection = {
  catalog: 'ProdAF',
  code: 'PI-MAIN',
  collectionSchedules: 3,
  endpoint: 'https://pi.example.invalid/piwebapi',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 12,
  transport: 'PiWebApi',
};

const Entity = {
  id: 1,
  code: 'FLD-1',
  displayName: 'Field weather feed',
  entityPath: null,
  isActive: true,
  lastRun: null,
  oldestGap: null,
  transport: 'Rest',
};

/** Та сама форма, якою сервер відмовляє в праві (`ECR-AUTH-0403`). */
const EntitiesForbidden = {
  title: 'Access denied',
  status: 403,
  detail: 'Requires permission Integration.Manage',
  errorCode: 'ECR-AUTH-0403',
  correlationId: 'corr-sources-403',
  messageKey: 'err.ECR-AUTH-0403.permission',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Сервер: з'єднання — за View або Manage; сутності — лише за Manage. */
function serve(permissions: string[]): void {
  const has = (permission: string): boolean => permissions.includes(permission);

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

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

      if (path.endsWith('/api/v1/data-sources')) {
        return has('Integration.View') || has('Integration.Manage') ? json([Connection]) : json(EntitiesForbidden, 403);
      }

      if (path.endsWith('/api/v1/sources')) {
        return has('Integration.Manage') ? json([Entity]) : json(EntitiesForbidden, 403);
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
        <MemoryRouter initialEntries={['/admin/sources']}>
          <SourcesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Таблиця сутностей збору — єдина таблиця сторінки поза секцією з'єднань. */
function entitiesTable(): HTMLElement | null {
  return (
    [...document.querySelectorAll<HTMLElement>('[data-table-state]')].find(
      (node) => node.closest('[data-data-sources]') === null,
    ) ?? null
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourcesPage лише з Integration.View', () => {
  it("з'єднання видно, кнопок дій немає", async () => {
    serve(['Integration.View']);
    show();

    await screen.findByText('Main PI server');

    // Дочекатися профілю: інакше «кнопки немає» було б правдою лише тому,
    // що `useSession` ще не відповів.
    await waitFor(() => expect(entitiesTable()?.querySelector('[role="alert"]')).toBeTruthy());

    expect(document.querySelector('[data-new-connection]')).toBeNull();
    expect(screen.queryByRole('button', { name: /sources\.collect|collect/i })).toBeNull();
  });

  it('сутності збору показують відмову з кодом, а не «сутностей немає»', async () => {
    serve(['Integration.View']);
    show();

    const alert = await waitFor(() => {
      const node = entitiesTable()?.querySelector<HTMLElement>('[role="alert"]');
      expect(node).toBeTruthy();

      return node as HTMLElement;
    });

    expect(alert.textContent).toContain('ECR-AUTH-0403');
    expect(entitiesTable()?.textContent ?? '').not.toMatch(/sources\.empty/);

    // Сторінка не зламалась: з'єднання поруч на місці.
    expect(await screen.findByText('Main PI server')).toBeTruthy();
  });
});

describe('SourcesPage лише з Integration.Manage', () => {
  it("усе доступне: з'єднання, «нове з'єднання», сутності й «Collect»", async () => {
    serve(['Integration.Manage']);
    show();

    await screen.findByText('Main PI server');
    await screen.findByText('Field weather feed');

    await waitFor(() => expect(document.querySelector('[data-new-connection]')).not.toBeNull());

    const row = screen.getByText('Field weather feed').closest('tr') as HTMLElement;
    expect(within(row).getByRole('button')).toBeTruthy();
    expect(entitiesTable()?.querySelector('[role="alert"]')).toBeNull();
  });
});
