import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
let jobState: 'Running' | 'Succeeded' = 'Running';

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

function show(): HTMLElement {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const { container } = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ExportButton documentId={1} periodKey={202601} language="en" />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return container;
}

/**
 * «Відбиток» того, що експорт кладе в рядок кнопок: теги прямих дітей
 * кореня. Саме пряма дитина рядка й займає в ньому місце — новий елемент
 * тут і є те, що переповнювало рядок у `U-25`.
 */
function rowFootprint(container: HTMLElement): string[] {
  return [...container.children].flatMap((root) => [
    root.tagName,
    ...[...root.children].map((child) => child.tagName),
  ]);
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
  jobState = 'Running';
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

describe('U-25 · стан експорту не змінює рядка кнопок', () => {
  it('ні задача в роботі, ні готовий файл не додають елемента в рядок', async () => {
    mockServer();
    const container = show();

    const idle = rowFootprint(container);

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: '⟦document.export⟧' }));

    await waitFor(() =>
      expect(screen.getByRole('button').getAttribute('data-export-state')).toBe('running'),
    );
    expect(rowFootprint(container)).toEqual(idle);

    jobState = 'Succeeded';

    // ⛔ Готовий файл приходить тостом (`notifications.show`), а не
    // посиланням у рядку: посилання в рядку й було тим елементом, що
    // виштовхував «Delete document» на другий рядок.
    await waitFor(() => expect(notifications.show).toHaveBeenCalled(), { timeout: 10_000 });

    expect(rowFootprint(container)).toEqual(idle);
    expect(screen.queryByRole('link')).toBeNull();
  }, 20_000);

  it('кнопка в роботі — підпис ПОРУЧ зі спінером, а не сам спінер, і місце під обидва підписи зарезервоване', async () => {
    mockServer();
    show();

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: '⟦document.export⟧' }));

    const button = screen.getByRole('button');
    await waitFor(() => expect(button.getAttribute('data-export-state')).toBe('running'));

    // ⛔ `loading` Mantine ховає підпис і лишає сам спінер — «Export» у
    // роботі ставав безіменним колом. Тут `data-loading` бути не має.
    expect(button.hasAttribute('data-loading')).toBe(false);

    const running = within(button).getByTestId('export-running-label');
    expect(running.textContent).toContain('⟦document.exportBuilding⟧');
    expect(running.getAttribute('aria-hidden')).toBe('false');

    // ⛔ Підпис спокою лишається в DOM (прихований), тобто ширина кнопки не
    // падає до ширини одного з підписів і не стрибає між станами.
    expect(button.textContent).toContain('⟦document.export⟧');
  });
});
