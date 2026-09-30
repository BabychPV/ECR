import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { Profiler, type ComponentProps, type JSX } from 'react';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { UiStringsPage } from '../UiStringsPage';
import { testTheme } from '@/test/render';

/**
 * Живий дефект (2026-09-24, замір на стенді): друк у «Filter by key» на
 * `/admin/ui-strings` — максимум 227 мс на символ (dev). Причина: фільтр
 * застосовувався синхронно, і кожне натискання в ТОМУ САМОМУ рендері, що й
 * оновлення поля, перемальовувало таблицю каталогу (до 100 рядків).
 *
 * ⚠ Два твердження, і кожне падає окремо:
 * 1. є коміт, у якому поле вже несе новий символ, а таблиця ще НЕ
 *    рендерилася — тобто поле не чекає таблиці (`useDeferredValue` + `memo`);
 * 2. після того, як таблиця наздогнала, рядки — рівно ті, що відповідають
 *    фільтру (без урахування регістру), і лічильник з ними згоден.
 *
 * ⛔ Мутаційний доказ: фільтр від `filter` замість `deferredFilter` (або без
 * `memo` на `UiStringsTable`) — таблиця рендериться в кожному коміті разом із
 * полем, і (1) червоний.
 */

const renders = vi.hoisted(() => ({ table: 0 }));

vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();
  const Real = actual.Table;

  function SpyTable(props: ComponentProps<typeof Real>): JSX.Element {
    renders.table += 1;
    return <Real {...props} />;
  }

  return { ...actual, Table: Object.assign(SpyTable, Real) };
});

const Strings: Record<string, string> = {
  'security.role': 'Role',
  'security.roles': 'Roles',
  'security.users': 'Users',
  'sources.never': 'Never',
  'uiStrings.title': 'Interface texts',
  'common.save': 'Save',
};

interface Commit {
  value: string;
  tableRenders: number;
}

let commits: Commit[] = [];

beforeEach(() => {
  commits = [];
  renders.table = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/languages')
        ? [{ code: 'en', nameNative: 'English' }]
        : url.includes('/me')
          ? { userId: 0, userName: 'test', language: 'en', permissions: [], isSimulation: false }
          : url.includes('/coverage')
            ? { languages: [] }
            : { languageCode: 'en', revision: 1, strings: Strings };

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Поле «Filter by key» (мітка — неперекладений ключ: каталог інтерфейсу тут не піднімається). */
function filterInput(): HTMLInputElement {
  return screen.getByLabelText(/uiStrings\.filter/) as HTMLInputElement;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/ui-strings']}>
          {/* ⚠ `onRender` викликається у фазі коміту, коли DOM уже оновлено:
              тут видно, що саме побачила людина в цьому кадрі. */}
          <Profiler
            id="page"
            onRender={() => {
              const field = screen.queryByLabelText(/uiStrings\.filter/) as HTMLInputElement | null;
              commits.push({ value: field?.value ?? '', tableRenders: renders.table });
            }}
          >
            <UiStringsPage />
          </Profiler>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function shownKeys(): string[] {
  const table = screen.getByRole('table');

  return within(table)
    .getAllByRole('row')
    .slice(1)
    .map((row) => within(row).getAllByRole('cell')[0]?.textContent ?? '');
}

describe('UiStringsPage: «Filter by key» не чекає перерендеру таблиці', () => {
  it('поле оновлюється в коміті без рендеру таблиці, а таблиця наздоганяє правильними рядками', async () => {
    show();
    await screen.findByText('security.role');

    let value = '';
    for (const ch of 'SECURITY.R') {
      value += ch;
      const before = renders.table;
      const from = commits.length;

      fireEvent.change(filterInput(), { target: { value } });

      // (1) Є коміт, де поле вже несе `value`, а таблиця ще не рендерилася.
      const urgent = commits.slice(from).find((commit) => commit.value === value);
      expect(urgent, `коміт із полем «${value}»`).toBeDefined();
      expect(urgent?.tableRenders, `таблиця в терміновому коміті «${value}»`).toBe(before);
    }

    // (2) Таблиця наздогнала — і показує рівно відфільтроване.
    expect(filterInput().value).toBe('SECURITY.R');
    expect(shownKeys()).toEqual(['security.role', 'security.roles']);
    expect(screen.getByTestId('ui-strings-count').textContent).toBe('2 / 2');

    fireEvent.change(filterInput(), { target: { value: 'users' } });
    expect(shownKeys()).toEqual(['security.users']);
  });
});
