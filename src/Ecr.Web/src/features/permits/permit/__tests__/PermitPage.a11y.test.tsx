import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { apiFetch } from '@/api/client';
import { PermitPage } from '@/pages/admin/PermitPage';
import * as rowsApi from '@/features/registries/rows/api';
import { Catalog, RouteShell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import * as permitsApi from '../../api/permitsApi';
import * as typesApi from '../../api/typesApi';
import { permit, permitType, rowsFor } from './permitFixtures';

/**
 * Доступність екрана дозволу в обох темах (`ФВ-14.16`, `D-127`): Overview-форма, Overview лише для читання,
 * таблиця з рядками, шторка рядка, діалог вставки з зауваженнями, діалог Activate. Підписи — зі СПРАВЖНЬОГО каталогу.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('../../api/permitsApi', () => ({
  getPermit: vi.fn(),
  updatePermit: vi.fn(),
  activatePermit: vi.fn(),
  endPermit: vi.fn(),
  renewPermit: vi.fn(),
  deletePermit: vi.fn(),
}));
vi.mock('../../api/typesApi', () => ({ getPermitType: vi.fn() }));
vi.mock('@/features/registries/rows/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/registries/rows/api')>()),
  getRegistryRows: vi.fn(),
  saveBatch: vi.fn(),
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Registry.EditData'], isSimulation: false } }),
}));

beforeEach(() => {
  vi.mocked(apiFetch).mockImplementation((path: string) =>
    Promise.resolve(path === '/api/v1/units' ? [{ id: 7, code: 't/yr' }] : []) as never,
  );
  vi.mocked(typesApi.getPermitType).mockResolvedValue(permitType());
  vi.mocked(rowsApi.getRegistryRows).mockImplementation((code, query) => Promise.resolve(rowsFor(code, query?.parentEntryId)));
});
afterEach(() => cleanup());

async function scan(colorScheme: 'light' | 'dark', entry: string, over = permit(), ready: () => Promise<unknown>): Promise<void> {
  vi.mocked(permitsApi.getPermit).mockResolvedValue(over);
  const { container } = render(
    <RouteShell colorScheme={colorScheme} path="/admin/permits/:id" entry={entry}>
      <PermitPage />
    </RouteShell>,
  );
  await screen.findByRole('heading', { level: 1, name: over.number });
  await ready();
  // Дати — лінивий чанк `@mantine/dates`: скануємо готове поле, а не скелет.
  await waitFor(() => expect(container.querySelector('.mantine-Skeleton-root')).toBeNull());
  await waitFor(() => expect(document.body.textContent).not.toContain('⟦'));
  const violations = await findViolations(document.body);
  expect(violations, describeViolations(violations)).toHaveLength(0);
}

describe('Екран дозволу — доступність', { timeout: 180_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: Overview з формою шапки (чернетка)`, async () => {
      await scan(colorScheme, '/admin/permits/5', permit(), () => screen.findByRole('form', { name: 'Permit details' }));
    });

    it(`${colorScheme}: Overview закінченого дозволу лише для читання`, async () => {
      await scan(colorScheme, '/admin/permits/5', permit({ status: 'Expired', actualEnd: '2026-09-01' }), () =>
        screen.findByText('This permit has expired and is read-only here'),
      );
    });

    it(`${colorScheme}: таблиця з рядками і підсумком`, async () => {
      await scan(colorScheme, '/admin/permits/5?tab=Limits', permit(), () => screen.findByText('Outfall A'));
    });

    it(`${colorScheme}: шторка нового рядка`, async () => {
      await scan(colorScheme, '/admin/permits/5?tab=Limits&panel=Limits:__new', permit(), () =>
        screen.findByRole('dialog', { name: 'New row' }),
      );
    });

    it(`${colorScheme}: шторка наявного рядка з вибраними списками`, async () => {
      await scan(colorScheme, '/admin/permits/5?tab=Limits&panel=Limits:11', permit(), async () => {
        await screen.findByRole('dialog', { name: 'Outfall A' });
        await screen.findByDisplayValue('Pond 1');
      });
    });

    it(`${colorScheme}: діалог вставки із зауваженнями`, async () => {
      await scan(colorScheme, '/admin/permits/5?tab=Limits', permit(), async () => {
        await screen.findByText('Outfall A');
        fireEvent.click(screen.getByRole('button', { name: 'Paste from Excel…' }));
        await screen.findByRole('dialog', { name: 'Paste rows into Limits' });
        fireEvent.change(screen.getByRole('textbox', { name: 'Rows' }), { target: { value: 'A\t12abc\nB\t3' } });
        await screen.findByText('Row 1, Annual volume: "12abc" is not a number');
      });
    });

    // PA2-U-02: з ВІДКРИТИМ календарем axe бачив button-name (prev/next) і color-contrast (дні сусідніх місяців).
    it(`${colorScheme}: відкритий календар поля дати (Valid from)`, async () => {
      await scan(colorScheme, '/admin/permits/5', permit(), async () => {
        await screen.findByRole('form', { name: 'Permit details' });
        const field = await screen.findByRole('textbox', { name: /Valid from/ });
        fireEvent.click(field);
        await screen.findByRole('button', { name: 'Previous month' });
      });
    });

    it(`${colorScheme}: діалог Activate`, async () => {
      await scan(colorScheme, '/admin/permits/5?dialog=activate', permit(), () =>
        screen.findByRole('dialog', { name: 'Activate permit KZ-100?' }),
      );
    });
  }
});
