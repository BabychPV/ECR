import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { PermitPage } from '@/pages/admin/PermitPage';
import * as rowsApi from '@/features/registries/rows/api';
import type { RegistryBatchResult } from '@/features/registries/rows/api';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { settle } from '@/test/closedDialogProbe';
import { TestProviders } from '@/test/render';
import * as permitsApi from '../../api/permitsApi';
import * as typesApi from '../../api/typesApi';
import { fakeImportServer, importResponse } from './importFixtures';
import { permit, permitType, PlacesRegistry, rowsFor } from './permitFixtures';

/**
 * B149: шторка рядка дозволу (`RowDrawer`) і «Paste from Excel…» (`PasteDialog`) — один запис на дію (зонд: ЗАКРИТО,
 * база 826f654e4; справжній власник — `PermitPage` → `TableTab`).
 *
 * - `RowDrawer` «Save»: `submit` звіряє `write.isPending`; «Delete row»: `disabled={remove.isPending}` з наступним
 *   рендером; власник монтує шторку лише за `{drawerHere && …}` (`<Drawer opened>`), успіх закриває її.
 * - `PasteDialog` «Add N rows»: `busy` ставиться синхронно в обробнику кліку (дискретна подія — рендер до наступного
 *   кліку), кнопка вимкнена, обробник звіряє `busy !== null`; успіх закриває діалог.
 *
 * Мутації (локально): без `write.isPending ||` у `submit` — червоний перший тест; `disabled={remove.isPending}` →
 * `false` — другий (`expected "vi.fn()" to be called 1 times, but got 2 times`); без `setBusy('apply')` — третій
 * (`expected [ true, true, true, false, false ] to deeply equal [ true, true, false ]`).
 */
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));
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
vi.mock('@/features/registries/rows/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/registries/rows/api')>()),
  getRegistryRows: vi.fn(),
  saveBatch: vi.fn(),
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Registry.EditData'], isSimulation: false } }),
}));

const saveBatch = vi.mocked(rowsApi.saveBatch);
const getRows = vi.mocked(rowsApi.getRegistryRows);

function applied(items: readonly { clientRowId: string }[], dryRun: boolean): RegistryBatchResult {
  return {
    added: items.length,
    applied: !dryRun,
    deleted: 0,
    dryRun,
    rows: items.map((item) => ({ clientRowId: item.clientRowId, entryId: 99, errors: [], status: 'added', version: 'v99' })),
    unchanged: 0,
    updated: 0,
  };
}

/** Тримає відповідь на запис, доки тест не відпустить. */
function holdBatch(): () => void {
  let release: () => void = () => undefined;
  saveBatch.mockImplementation(
    (_code, items, dryRun) =>
      new Promise((resolve) => {
        release = () => resolve(applied(items, dryRun));
      }),
  );

  return () => {
    release();
  };
}

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

/*
 * ⛔ Правило 386 / F13: під навантаженням падало 6/33. Причина — холодні ліниві чанки (`TableTab` сторінки,
 * `RowDrawer`/`PasteDialog` вкладки, поле дати): перше `import()` під навантаженням довше за 1 с бюджету `findBy`.
 * Прогрів тут, до першого тесту; компоненти далі монтуються тим самим `lazy()` із уже теплого кешу модулів.
 */
beforeAll(async () => {
  await Promise.all([
    import('@/features/permits/permit/TableTab'),
    import('@/features/permits/permit/RowDrawer'),
    import('@/features/permits/permit/PasteDialog'),
    import('@/features/permits/permit/LifecycleDialogs'),
    import('@/shared/dates/DateInputWithStyles'),
  ]);
}, 60_000);

beforeEach(() => {
  const importServer = fakeImportServer();
  vi.mocked(apiFetch).mockImplementation((path: string, init?: RequestInit) =>
    Promise.resolve(path === '/api/v1/units' ? [{ id: 7, code: 't/yr' }] : (importResponse(importServer, path, init) ?? [])) as never,
  );
  vi.mocked(typesApi.getPermitType).mockResolvedValue(permitType());
  getRows.mockImplementation((code, query) =>
    query?.asOf === undefined ? Promise.reject(new Error('asOf is required')) : Promise.resolve(rowsFor(code, query.parentEntryId)),
  );
  vi.mocked(permitsApi.getPermit).mockResolvedValue(permit());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function openLimits(): Promise<void> {
  render(
    <TestProviders>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/permits/5?tab=Limits']}>
          <Routes>
            <Route path="/admin/permits/:id" element={<PermitPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
  await screen.findByText('Outfall A', );
}

describe('B149: RowDrawer і PasteDialog — один запис на дію', () => {
  it('RowDrawer: подвійний клік «Save» шле один пакет; після успіху шторку знято', async () => {
    const release = holdBatch();
    await openLimits();
    fireEvent.click(screen.getByRole('button', { name: 'Add row' }));
    await screen.findByRole('dialog', { name: 'New row' });
    fireEvent.change(screen.getByRole('textbox', { name: /^Name/ }), { target: { value: 'Outfall C' } });
    fireEvent.change(screen.getByRole('textbox', { name: /^Annual volume/ }), { target: { value: '5' } });
    const save = screen.getByRole('button', { name: 'Save' });

    fireEvent.click(save);
    await waitFor(() => {
      expect(saveBatch).toHaveBeenCalledTimes(1);
    });
    await nextTick();
    fireEvent.click(save);
    await settle();
    expect(saveBatch).toHaveBeenCalledTimes(1);

    await act(async () => {
      release();
    });
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'New row' })).toBeNull();
    });
    expect(saveBatch).toHaveBeenCalledTimes(1);
  });

  it('RowDrawer: подвійний клік «Delete row» шле один пакет delete', async () => {
    holdBatch();
    await openLimits();
    fireEvent.click(screen.getByText('Outfall B'));
    const dialog = await screen.findByRole('dialog', { name: 'Outfall B' });
    const remove = within(dialog).getByRole('button', { name: 'Delete row' });

    fireEvent.click(remove);
    await waitFor(() => {
      expect(saveBatch).toHaveBeenCalledTimes(1);
    });
    await nextTick();
    fireEvent.click(remove);
    await settle();
    expect(saveBatch).toHaveBeenCalledTimes(1);
  });

  it('PasteDialog: подвійний клік «Add N rows» запускає один запис; після успіху діалог знято', async () => {
    saveBatch.mockImplementation((_code, items, dryRun) => Promise.resolve(applied(items, dryRun)));
    await openLimits();
    fireEvent.click(screen.getByRole('button', { name: 'Paste from Excel…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Paste rows into Limits' });
    await waitFor(() => {
      expect(getRows).toHaveBeenCalledWith(PlacesRegistry, expect.anything());
    });
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Rows' }), { target: { value: ['Outfall C\t5', 'Outfall D\t7'].join('\n') } });
    const check = within(dialog).getByRole('button', { name: 'Check rows' });
    await waitFor(() => {
      expect(check).toHaveProperty('disabled', false);
    });
    fireEvent.click(check);
    const add = await within(dialog).findByRole('button', { name: 'Add 2 rows' });
    await waitFor(() => {
      expect(add).toHaveProperty('disabled', false);
    });

    fireEvent.click(add);
    fireEvent.click(add);
    await nextTick();
    fireEvent.click(add);
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Paste rows into Limits' })).toBeNull();
    });
    await settle();
    // Перевірка → повторна перевірка перед записом → один запис (без dryRun).
    expect(saveBatch.mock.calls.map((call) => call[2])).toEqual([true, true, false]);
  });
});
