import { describe, it, expect, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReportDefinition } from '@/api/types';
import { ReportDefinitionsModal } from '@/features/reports/ReportDefinitionsModal';
import { testTheme } from '@/test/render';

// ⚠ Не-публікаційні запити (перелік мов для LocalizedInput тощо) отримують `[]`, а не `{}`.
const apiFetch = vi.hoisted(() =>
  vi.fn((path: string, _init?: RequestInit) =>
    Promise.resolve(path.endsWith('/publish') ? {} : []),
  ),
);
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch,
}));

/**
 * ФВ-14.7: публікація версії звіту вимагає причини — кнопка лише відкриває діалог, запит іде
 * з `reason`, а порожня причина його не відправляє.
 *
 * Мутаційні докази: прибрати `reason` із тіла — другий крок червоніє; повернути прямий
 * `publish.mutate` на кнопці — діалогу немає, перший крок червоніє.
 */
const definitions: ReportDefinition[] = [
  {
    id: 1,
    code: 'FORM_6',
    isActive: true,
    isRegulatory: true,
    nameL10n: { values: { en: 'Form 6' } },
    versions: [
      {
        id: 3,
        version: '3.0',
        status: 'Draft',
        columnsJson: '[]',
        rulesJson: '{}',
        createdAt: '2026-09-19T10:00:00Z',
      },
    ],
  },
];

const SlowEnvTimeout = 400_000;

function publishCalls(): unknown[][] {
  return apiFetch.mock.calls.filter(([path]) => path.endsWith('/publish'));
}

beforeEach(() => {
  apiFetch.mockClear();
});

describe('ReportDefinitionsModal: причина публікації версії', () => {
  it(
    'кнопка відкриває діалог; без причини запиту немає; з причиною тіло містить reason',
    async () => {
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      render(
        <MantineProvider theme={testTheme}>
          <QueryClientProvider client={client}>
            <ReportDefinitionsModal opened onClose={() => undefined} definitions={definitions} />
          </QueryClientProvider>
        </MantineProvider>,
      );

      const row = (await screen.findByText('FORM_6', {}, { timeout: SlowEnvTimeout })).closest('tr');
      expect(row).not.toBeNull();

      // Крок 1: кнопка НЕ шле запит — відкриває діалог причини.
      fireEvent.click(screen.getByRole('button', { name: /reportDefs\.publish⟧/ }));
      expect(publishCalls()).toHaveLength(0);

      const reasonField = await screen.findByLabelText('⟦reportDefs.publishReason⟧');
      const confirm = screen
        .getAllByRole('button', { name: /reportDefs\.publish⟧/ })
        .find((button) => button.closest('[role="dialog"]') !== row?.closest('[role="dialog"]'));
      expect(confirm, 'кнопка підтвердження діалогу причини').toBeDefined();
      expect((confirm as HTMLButtonElement).disabled).toBe(true);

      // Крок 2: з причиною — запит із reason у тілі.
      fireEvent.change(reasonField, { target: { value: '  Затверджено комісією  ' } });
      expect((confirm as HTMLButtonElement).disabled).toBe(false);
      fireEvent.click(confirm as HTMLButtonElement);

      await waitFor(() => {
        expect(publishCalls()).toHaveLength(1);
      });
      const [path, init] = publishCalls()[0] as [string, RequestInit];
      expect(path).toBe('/api/v1/reports/1/versions/3/publish');
      expect(init.method).toBe('POST');
      expect(JSON.parse(init.body as string)).toEqual({ reason: 'Затверджено комісією' });
    },
    SlowEnvTimeout,
  );
});
