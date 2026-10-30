import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ImportPreview } from '@/api/types';
import { ImportPanel } from '@/features/import/ImportPanel';
import { testTheme } from '@/test/render';

/**
 * ✎ D-461: перегляд імпорту КАЖЕ, що значення з книги ляже ручним замість формули (`replacesFormula`), а порожнє
 * значення в такій комірці поверне формулу. Позначка — словом (не лише кольором), пояснення — підказкою.
 *
 * ⚠ Відповідь прев'ю підмінено на рівні `fetch` (як `ImportPanelRounding.test.tsx`): предмет — показ готової
 * відповіді сервера.
 */

function mockPreview(preview: ImportPreview): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/import/preview')) {
        return new Response(JSON.stringify(preview), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

async function openPreview(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { container } = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ImportPanel documentId={1} periodKey={202610} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  const input = container.querySelector('input[type="file"]');
  if (!(input instanceof HTMLInputElement)) throw new Error('немає поля вибору файлу');

  await userEvent.upload(input, new File(['x'], 'book.xlsx'));
  await screen.findByRole('dialog');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ImportPanel: значення замість формули (D-461)', () => {
  it('replacesFormula — позначка «замінить формулу»; порожнє значення — «поверне формулу»; звичайна зміна — без позначки', async () => {
    mockPreview({
      previewToken: 'tok',
      changes: [
        { rowKey: 'R1', columnCode: 'C1', oldValue: '10', newValue: 12, tableCode: 'T1', replacesFormula: true },
        { rowKey: 'R2', columnCode: 'C1', oldValue: '11', newValue: null, tableCode: 'T1', replacesFormula: true },
        { rowKey: 'R3', columnCode: 'C1', oldValue: '1', newValue: 2, tableCode: 'T1', replacesFormula: false },
        { rowKey: 'R4', columnCode: 'C1', oldValue: '1', newValue: 3, tableCode: 'T1' },
      ],
      rejected: [],
      conflicts: [],
      overwritable: [],
    });

    await openPreview();

    const marks = [...document.querySelectorAll('[data-import-replaces-formula]')] as HTMLElement[];
    expect(marks.map((mark) => mark.getAttribute('data-import-replaces-formula'))).toEqual(['override', 'restore']);

    // Словом, а не кольором; пояснення — підказкою.
    expect(marks[0]!.textContent).toBe('⟦import.replacesFormula⟧');
    expect(marks[0]!.getAttribute('title')).toBe('⟦import.replacesFormulaHint⟧');
    expect(marks[1]!.textContent).toBe('⟦import.restoresFormula⟧');
    expect(marks[1]!.getAttribute('title')).toBe('⟦import.restoresFormulaHint⟧');

    // Позначка стоїть у рядку своєї зміни.
    expect(marks[0]!.closest('tr')?.textContent).toContain('R1');
    expect(marks[1]!.closest('tr')?.textContent).toContain('R2');

    // Заміна формули — не відмова: застосувати можна.
    expect(screen.getByRole('button', { name: '⟦import.apply⟧' }).hasAttribute('disabled')).toBe(false);
  });
});
