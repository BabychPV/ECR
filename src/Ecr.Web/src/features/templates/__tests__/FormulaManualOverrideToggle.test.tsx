import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/app/queryClient';
import { FormulaManualOverrideToggle } from '@/features/templates/FormulaManualOverrideToggle';
import { testTheme } from '@/test/render';

/**
 * ✎ D-461: «Дозволити ручне значення». Увімкнення — один виклик; вимкнення — спершу `dryRun`, і якщо ручні значення
 * вже є, людина бачить кількість і підтверджує; без них — вимикається одразу.
 *
 * ⚠ Сервер підмінено на рівні `fetch` (як `VersionMigrationDialog.formulaOverride.test.tsx`).
 */
const calls: { allow: boolean; dryRun: boolean }[] = [];
let overriddenCells = 0;

function stubServer(): void {
  calls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown): Promise<Response> =>
        Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

      if (url.includes('/formulas/77/manual-override') && init?.method === 'PUT') {
        const body = JSON.parse(String(init.body)) as { allow: boolean; dryRun: boolean };
        calls.push(body);

        return json({
          formulaId: 77,
          allowManualOverride: body.dryRun ? true : body.allow,
          changed: !body.dryRun,
          overriddenDocuments: overriddenCells > 0 ? 3 : 0,
          overriddenCells,
        });
      }

      return json({});
    }),
  );
}

function show(allow: boolean): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={createQueryClient()}>
        <FormulaManualOverrideToggle templateVersionId={5} formulaId={77} allow={allow} disabled={false} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function checkbox(): HTMLInputElement {
  return document.querySelector('input[data-formula-manual-override="77"]') as HTMLInputElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
  overriddenCells = 0;
});

describe('FormulaManualOverrideToggle', () => {
  it('увімкнення — один виклик без dryRun', async () => {
    stubServer();
    show(false);
    fireEvent.click(checkbox());
    await waitFor(() => expect(calls).toEqual([{ allow: true, dryRun: false }]));
  });

  it('вимкнення без ручних значень — dryRun, потім одразу справжній виклик', async () => {
    stubServer();
    show(true);
    fireEvent.click(checkbox());
    await waitFor(() =>
      expect(calls).toEqual([
        { allow: false, dryRun: true },
        { allow: false, dryRun: false },
      ]),
    );
  });

  it('вимкнення з ручними значеннями — попередження з кількістю, справжній виклик лише після підтвердження', async () => {
    overriddenCells = 5;
    stubServer();
    show(true);
    fireEvent.click(checkbox());

    await screen.findByText(/formulas\.disableManualOverrideBody/);
    expect(screen.getByText(/documents.*3/)).toBeTruthy();
    expect(calls).toEqual([{ allow: false, dryRun: true }]);

    fireEvent.click(screen.getByRole('button', { name: /formulas\.disableManualOverrideVerb/ }));
    await waitFor(() => expect(calls).toEqual([
      { allow: false, dryRun: true },
      { allow: false, dryRun: false },
    ]));
  });
});
