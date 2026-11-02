import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { apiFetch } from '@/api/client';
import { PermitPage } from '@/pages/admin/PermitPage';
import { Catalog, RouteShell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import * as permitsApi from '../../api/permitsApi';
import * as typesApi from '../../api/typesApi';
import * as usagesApi from '../../api/usagesApi';
import { permit, permitType } from '../../permit/__tests__/permitFixtures';
import { cellItem, headerItem, usage } from './usageFixtures';

/**
 * Доступність вкладки «Used in» картки дозволу в обох темах (`ФВ-14.16`, `D-127`): перелік місць зі смугою обрізаного
 * переліку і фільтром періоду; порожній стан. Підписи — зі СПРАВЖНЬОГО каталогу.
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
vi.mock('@/features/permits/api/permitsApi', () => ({
  getPermit: vi.fn(),
  updatePermit: vi.fn(),
  activatePermit: vi.fn(),
  endPermit: vi.fn(),
  renewPermit: vi.fn(),
  deletePermit: vi.fn(),
}));
vi.mock('@/features/permits/api/typesApi', () => ({ getPermitType: vi.fn() }));
vi.mock('@/features/permits/api/usagesApi', () => ({ getPermitUsages: vi.fn() }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View'], isSimulation: false } }),
}));

beforeEach(() => {
  vi.mocked(apiFetch).mockImplementation(() => Promise.resolve([]) as never);
  vi.mocked(typesApi.getPermitType).mockResolvedValue(permitType());
  vi.mocked(permitsApi.getPermit).mockResolvedValue(permit());
});
afterEach(() => cleanup());

async function scan(colorScheme: 'light' | 'dark', ready: () => Promise<unknown>): Promise<void> {
  const { container } = render(
    <RouteShell colorScheme={colorScheme} path="/admin/permits/:id" entry="/admin/permits/5?tab=usage">
      <PermitPage />
    </RouteShell>,
  );
  await ready();
  await waitFor(() => expect(container.querySelector('.mantine-Skeleton-root')).toBeNull());
  await waitFor(() => expect(document.body.textContent).not.toContain('⟦'));
  const violations = await findViolations(document.body);
  expect(violations, describeViolations(violations)).toHaveLength(0);
}

describe('Вкладка «Used in» — доступність', { timeout: 180_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: місця (шапка і комірка) з обрізаним переліком`, async () => {
      vi.mocked(usagesApi.getPermitUsages).mockResolvedValue(usage([headerItem(), cellItem()], { truncated: true, linkCount: 900 }));
      await scan(colorScheme, () => screen.findByRole('table', { name: 'Places where the permit is used' }));
    });

    it(`${colorScheme}: порожньо`, async () => {
      vi.mocked(usagesApi.getPermitUsages).mockResolvedValue(usage([]));
      await scan(colorScheme, () => screen.findByText('Not used in any document yet'));
    });
  }
});
