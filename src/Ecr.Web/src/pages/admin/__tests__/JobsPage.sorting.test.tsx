import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * Перелік задач переведено на `DataTable` набору (`UI-06`, шар 3) — і разом із
 * таблицею на екран прийшло СОРТУВАННЯ, якого тут не було.
 *
 * ⛔ Це нова поведінка, тож вона має власний тест: п'ять наявних наборів
 * `JobsPage.*` перевіряють те, що було до переходу, і всі п'ять лишилися
 * зеленими без жодної правки — саме це й доводить, що переїзд нічого не
 * зламав. Але жоден із них не побачив би, якби сортування мовчки не робило
 * нічого.
 *
 * ⚠ Найтихіший спосіб «мати сортування» — клікабельна шапка, що переставляє
 * лише стрілку. Тому тут порівнюється ПОРЯДОК РЯДКІВ, а не стан шапки.
 */

const jobs = [
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: 'IRecalculationJob#42',
    percent: 40,
    startedAt: '2026-09-19T09:58:00Z',
    state: 'Running',
    updatedAt: '2026-09-19T10:00:00Z',
  },
  {
    jobCode: 'Ecr.Application.Ports.IExcelExportJob',
    jobId: 'IExcelExportJob-alien',
    percent: 100,
    startedAt: '2026-09-19T08:55:00Z',
    state: 'Succeeded',
    updatedAt: '2026-09-19T09:00:00Z',
  },
];

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/jobs')) {
        return Promise.resolve(
          new Response(JSON.stringify(jobs), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      return Promise.resolve(new Response(JSON.stringify(null), { status: 404 }));
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/admin/jobs']}>
        <QueryClientProvider client={client}>
          <JobsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Порядок станів у переліку зверху вниз — він і є порядком рядків. */
function statesInOrder(): readonly string[] {
  return [...document.querySelectorAll('[data-status-state]')].map(
    (node) => node.getAttribute('data-status-state') ?? '',
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: перелік на DataTable набору', () => {
  it('клац по шапці «Стан» переставляє РЯДКИ, а не лише стрілку', async () => {
    mockFetch();
    const user = userEvent.setup();
    show();

    // Порядок, у якому відповів сервер: спершу Running, потім Succeeded.
    await waitFor(() => expect(statesInOrder()).toEqual(['Running', 'Succeeded']));

    await user.click(screen.getByRole('button', { name: '⟦jobs.recentState⟧' }));

    /*
     * ⛔ Мутаційний доказ: поставте колонці стану `sortable: false` — шапка
     * перестане бути кнопкою, і `getByRole('button', …)` вище не знайде її
     * зовсім. Поміняйте `key` на будь-яке інше ім'я — `DataTable` візьме
     * `row['інше']`, тобто `undefined` для обох рядків, і обидва порівняння
     * нижче дадуть той самий порядок, у якому відповів сервер.
     *
     * ⚠ А ось прибирання `sortValue` в цій колонці НЕ ламає нічого, і це
     * перевірено, а не припущено: ключ колонки — `state`, тож `DataTable`
     * бере `row['state']` сам. Саме тому зайвого пропа в коді немає: він
     * виглядав би як необхідний.
     */
    await waitFor(() => expect(statesInOrder()).toEqual(['Running', 'Succeeded']));

    await user.click(screen.getByRole('button', { name: '⟦jobs.recentState⟧' }));

    await waitFor(() => expect(statesInOrder()).toEqual(['Succeeded', 'Running']));
  });

  it('колонка дій не сортується — у кнопок немає значення, за яким порівнювати', async () => {
    /*
     * ⚠ Це не дрібниця вигляду. Клікабельна шапка над колонкою кнопок
     * обіцяє дію, якої не буде: порівнювати `undefined` з `undefined` —
     * означає мовчки не робити нічого, і користувач вирішить, що зламався
     * перелік, а не що сортувати тут нічим.
     */
    mockFetch();
    show();

    await screen.findByRole('table');

    const headers = screen.getAllByRole('columnheader');
    const actions = headers[headers.length - 1];

    // ⚠ Спершу переконуємося, що клікабельні шапки взагалі є — інакше
    // твердження лишилося б зеленим і на таблиці, де не сортується НІЩО.
    expect(headers[0]?.querySelector('button')).not.toBeNull();
    expect(actions?.querySelector('button')).toBeNull();
  });
});
