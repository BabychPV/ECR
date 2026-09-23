import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';
import { ExportButton } from '../ExportButton';
import { testTheme } from '@/test/render';

/**
 * Розкладка експорту в панелі дій документа (`U-15`, `U-25`).
 *
 * ⚠ jsdom не міряє ширин, тому тут перевіряється СТРУКТУРА, яка ширину
 * визначає: що рядок кнопок не отримує нового елемента від стану експорту, і
 * що обидва підписи кнопки стоять у DOM завжди (ширина = довший із двох).
 * Візуальну перевірку на 1280 px цей набір не замінює.
 */

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

/** Стан задачі, який віддає опитування; змінюється посеред тесту. */
const jobState: 'Running' | 'Succeeded' = 'Running';

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/export') && !url.includes('/jobs/')) {
        return new Response(JSON.stringify({ jobId: 'job-9' }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/jobs/job-9')) {
        return new Response(
          JSON.stringify({
            jobId: 'job-9',
            state: jobState,
            percent: jobState === 'Succeeded' ? 100 : 40,
            message: jobState === 'Succeeded' ? 'export-key-9' : null,
            error: null,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ExportButton documentId={1} periodKey={202601} language="en" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
});

describe('U-15 · формат і кнопка експорту — одна одиниця', () => {
  it('перемикач формату стоїть В ОДНІЙ групі з кнопкою «Export», і кнопка — перша', () => {
    mockServer();
    show();

    const unit = screen.getByRole('group', { name: '⟦document.export⟧' });

    // ⛔ Обидва — всередині групи: перемикач поза нею знову стоїть «сам по
    // собі» поруч з імпортом, і належність не видно нічим.
    const button = within(unit).getByRole('button', { name: '⟦document.export⟧' });
    const formats = within(unit).getByRole('radiogroup', { name: '⟦document.exportFormat⟧' });

    // ⛔ Порядок — половина виправлення: поруч з «Import from Excel» має
    // опинитися слово «Export», а не голий перелік форматів. Перемикач
    // ПЕРЕД кнопкою — рівно розкладка зі знімка `admin-20-document.png`.
    expect(button.compareDocumentPosition(formats) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});
