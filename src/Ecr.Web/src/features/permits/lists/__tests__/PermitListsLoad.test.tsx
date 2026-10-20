import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitListsPage } from '@/pages/admin/PermitListsPage';
import type { PermitListDto } from '../listsApi';
import { usedCountText } from '../listsModel';

/**
 * Завантаження екрана «Lists» (PM-08e): ОДИН запит `GET /permit-lists` на будь-яку кількість типів. Раніше екран читав
 * перелік типів і визначення КОЖНОГО типу окремо (аудит PA-U-16: 275 запитів, 4,6 с).
 *
 * Мутаційний доказ (вручну, відновлено): повернути в `useLists` читання `GET /permit-types/{code}` по кожному типу валить
 * «жодного читання визначень типів».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Permit.EditType'], isSimulation: false } }),
}));

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const Count = 14;

function lists(): PermitListDto[] {
  return Array.from({ length: Count }, (_, index) => {
    const code = `T${String(index + 1).padStart(2, '0')}`;

    return {
      key: `type:${code}:Items:Method${code}`,
      scope: 'TypeOnly',
      registryCode: `PRM_${code}_Items_Method${code}`,
      typeCode: code,
      nameL10n: { en: `Method${code}` },
      itemCount: 1,
      editable: false,
      usedBy: [
        {
          typeCode: code,
          typeNameL10n: { en: `Type ${code}` },
          tableCode: 'Items',
          tableNameL10n: { en: 'Items' },
          fieldCode: `Method${code}`,
          fieldNameL10n: { en: `Method${code}` },
        },
      ],
    };
  });
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/permits/lists']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="/admin/permits/lists" element={<PermitListsPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Lists: завантаження', () => {
  it(`${Count} типів - один запит переліку списків, жодного читання визначень типів`, async () => {
    const requests: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        requests.push(String(input));

        return json(lists());
      }),
    );
    show();

    expect(await screen.findByText('MethodT14')).toBeTruthy();
    expect(requests).toEqual(['/api/v1/permit-lists']);
    expect(requests.some((url) => url.includes('/permit-types'))).toBe(false);
  });
});

describe('usedCountText', () => {
  it('порожнє, одне й багато', () => {
    expect(usedCountText(0)).toBe('permits.lists.used.none');
    expect(usedCountText(1)).toBe('permits.lists.used.one');
    expect(usedCountText(3)).toBe('permits.lists.used.other {"count":3}');
  });
});
