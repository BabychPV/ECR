import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefinitionDto } from '@/api/types';
import type { UsageResponse } from '@/features/registries/api';
import { RegistryUsageList } from '@/features/registries/RegistryUsage';
import { RegistryConstructorPage } from '@/pages/admin/RegistryConstructorPage';
import { testTheme } from '@/test/render';

/**
 * Вкладка «Де використано» конструктора довідника — споживач
 * `GET /api/v1/registries/{code}/usage` (`BE-24`, директива №15).
 *
 * ⛔ Головний випадок — відмова (`L10`): «ніде не використано» — твердження, на
 * якому людина вирішує, що опис можна міняти вільно. На відмові запиту його
 * бути не може; замість нього — причина з кодом і «повторити».
 *
 * ⚠ Каталог рядків не завантажений: підписи приходять ключами в `⟦…⟧` разом
 * із параметрами (`⟦registries.usageShown (shown=2, total=45)⟧`), тож числа
 * перевіряються саме в тому вигляді, в якому їх отримав `t()`.
 */

const Definition: RegistryDefinitionDto = {
  id: 4,
  code: 'PERMIT',
  nameL10n: { values: { en: 'Permits' } },
  isTemporal: false,
  sourceKind: 'Local',
  definitionVersion: 3,
  dataRevision: 11,
  fields: [],
  relations: [],
  rules: [],
  mappings: [],
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

/** ⚠ Без `messageKey` подробиця до екрана не доходить (рішення про мову). */
const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'залежності довідника прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-usage-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

type Usage = UsageResponse | 'refuse';

function mockServer(usage: Usage, permissions: string[]): ReturnType<typeof vi.fn> {
  const fetch = vi.fn(async (input: RequestInfo | URL) => {
    const path = String(input).split('?')[0] ?? '';

    if (path.includes('/ui-strings/')) {
      return json({ languageCode: 'en', revision: 1, strings: {} });
    }

    // ⚠ `/api/v1/me` — ТОЧНО: `includes` збігся б і з `/api/v1/methodologies`.
    if (path.endsWith('/api/v1/me')) {
      return json({
        denies: [],
        grants: {},
        isSimulation: false,
        language: 'en',
        mustChangePassword: false,
        permissions,
        simulatedForUserId: null,
        userId: 9,
        userName: 'tester',
      });
    }

    if (path.endsWith('/api/v1/users')) {
      return json({ items: [], totalCount: 0 });
    }

    if (path.endsWith('/PERMIT/usage')) {
      return usage === 'refuse' ? json(Refusal, 500) : json(usage);
    }

    if (path.endsWith('/definition')) {
      return json(Definition);
    }

    if (path.endsWith('/history')) {
      return json([]);
    }

    if (path.endsWith('/api/v1/registries')) {
      return json([]);
    }

    return json(null);
  });

  vi.stubGlobal('fetch', fetch);

  return fetch;
}

function usageCalls(fetch: ReturnType<typeof vi.fn>): number {
  return fetch.mock.calls.filter(([input]) => String(input).includes('/usage')).length;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/registries/PERMIT/definition']}>
          <Routes>
            <Route
              path="/admin/registries/:code/definition"
              element={<RegistryConstructorPage />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const Editor = ['Registry.View', 'Registry.EditDefinition'];

async function openUsage(): Promise<void> {
  fireEvent.click(await screen.findByRole('tab', { name: /registries\.tabUsage/ }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Конструктор довідника: «де використано»', () => {
  it('посилання є і стеля — перелік, загальне число й «показано N із M»', async () => {
    mockServer(
      {
        total: 45,
        items: [
          { kind: 'templateColumn', id: '1234567', label: 'F2TP.Col7', route: '/admin/templates/12/versions/3' },
          { kind: 'registryField', id: '88', label: 'SITE.Permit', route: null },
        ],
      },
      Editor,
    );
    show();
    await openUsage();

    const list = await waitFor(() => {
      const found = document.querySelector('[data-registry-usage="list"]');
      if (found === null) throw new Error('перелік ще не намальовано');
      return found as HTMLElement;
    });

    expect(list.textContent).toContain('registries.usageTotal (total=45)');
    expect(list.textContent).toContain('registries.usageShown (shown=2, total=45)');

    // ⚠ Маршрут є — посилання саме туди; немає — текст без посилання.
    const link = within(list).getByRole('link', { name: 'F2TP.Col7' });
    expect(link.getAttribute('href')).toBe('/admin/templates/12/versions/3');
    expect(within(list).getByText('SITE.Permit').closest('a')).toBeNull();

    // ⛔ Ідентифікатор без роздільників розрядів.
    expect(within(list).getByText('1234567')).toBeDefined();

    expect(screen.queryByText(/registries\.usageNone/)).toBeNull();
  });

  it('усі посилання показано — «показано N із M» не малюється', async () => {
    mockServer(
      { total: 1, items: [{ kind: 'templateColumn', id: '7', label: 'F2TP.Col1', route: null }] },
      Editor,
    );
    show();
    await openUsage();

    await screen.findByText('F2TP.Col1');

    expect(screen.getByText(/registries\.usageTotal \(total=1\)/)).toBeDefined();
    expect(screen.queryByText(/registries\.usageShown/)).toBeNull();
  });

  it('посилань немає (успіх) — «ніде не використано»', async () => {
    mockServer({ total: 0, items: [] }, Editor);
    show();
    await openUsage();

    expect(await screen.findByText(/registries\.usageNone/)).toBeDefined();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('відмова — причина з кодом і «повторити», і НЕ «ніде не використано»', async () => {
    const fetch = mockServer('refuse', Editor);
    show();
    await openUsage();

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('залежності довідника прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

    // ⛔ Головне твердження: відмова не говорить від імені залежностей.
    expect(screen.queryByText(/registries\.usageNone/)).toBeNull();
    expect(screen.queryByText(/registries\.usageTotal/)).toBeNull();

    // «Повторити» справді перепитує сервер.
    const before = usageCalls(fetch);
    fireEvent.click(within(alert).getByRole('button'));
    await waitFor(() => {
      expect(usageCalls(fetch)).toBeGreaterThan(before);
    });
  });

  it('вкладка закрита за замовчуванням — запиту до відкриття немає', async () => {
    const fetch = mockServer({ total: 0, items: [] }, Editor);
    show();

    const tab = await screen.findByRole('tab', { name: /registries\.tabUsage/ });

    expect(tab.getAttribute('aria-selected')).toBe('false');
    expect(usageCalls(fetch)).toBe(0);
  });

  it('без права Registry.EditDefinition — вкладки немає і запиту немає', async () => {
    const fetch = mockServer({ total: 0, items: [] }, ['Registry.View']);
    show();

    // ⚠ Спершу дочекатися сторінки: інакше «вкладки немає» було б правдою на
    // будь-якому коді, поки опис у дорозі.
    await screen.findByRole('tab', { name: /registries\.tabHistory/ });

    expect(screen.queryByRole('tab', { name: /registries\.tabUsage/ })).toBeNull();
    expect(usageCalls(fetch)).toBe(0);
  });
});

/**
 * Рід залежного об'єкта — людською назвою з каталогу, не сирим `kind`.
 *
 * ⚠ Перелік — рівно ті п'ять видів (`UsageKinds`), які сервер пише в
 * `RegistryStore.GetUsageAsync`. Назва — зі спільного простору `usageKind.*`
 * (`UsageKindLabel`), того самого, що на екрані одиниць. Новий вид на сервері
 * без рядка тут покаже сире значення в `<code>` — не порожнечу.
 */
describe('«де використано»: назва роду залежного', () => {
  // ⚠ `data` — окремо нижче: у нього немає підпису-імені (X-11).
  const Kinds = [
    'templateColumn',
    'registryField',
    'methodologySubstance',
    'sourceEntity',
  ] as const;

  function badgeOf(label: string): HTMLElement {
    const item = screen.getByText(label).closest('li');
    if (item === null) throw new Error(`рядка ${label} немає`);
    // ⚠ Клас Mantine, а не власний атрибут: тоді «до» падає на ЗМІСТІ бейджа,
    // а не на тому, що селектор ще нічого не знаходить.
    const badge = item.querySelector('.mantine-Badge-root');
    if (badge === null) throw new Error(`бейджа роду в ${label} немає`);
    return badge as HTMLElement;
  }

  function list(items: UsageResponse['items']): void {
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <RegistryUsageList usage={{ total: items.length, items }} />
        </MemoryRouter>
      </MantineProvider>,
    );
  }

  it('кожен відомий рід — ключ каталогу, а не сире значення', () => {
    list(Kinds.map((kind, i) => ({ kind, id: String(i), label: `L-${kind}`, route: null })));

    for (const kind of Kinds) {
      const badge = badgeOf(`L-${kind}`);
      expect(badge.textContent).toBe(`⟦usageKind.${kind}⟧`);
    }
  });

  it('дані в документах — речення, а не ім\'я таблиці сховища (X-11)', () => {
    // ⛔ Сервер доти віддавав `doc.CellValue` і як підпис, і як ідентифікатор:
    // людина читала «STORED DATA · doc.CellValue».
    list([{ kind: 'data', id: 'doc.CellValue', label: 'doc.CellValue', route: null }]);

    const row = screen.getByText('⟦registries.usageDataInDocuments⟧').closest('li');
    expect(row).not.toBeNull();
    expect(row?.textContent).not.toContain('doc.CellValue');
    expect(row?.querySelector('.mantine-Badge-root')?.textContent).toBe('⟦usageKind.data⟧');
  });

  it('невідомий рід — сире значення в <code>, не порожньо й не вигадана назва', () => {
    list([{ kind: 'futureThing', id: '1', label: 'X.Y', route: null }]);

    const badge = badgeOf('X.Y');
    const code = badge.querySelector('code');
    expect(code?.textContent).toBe('futureThing');
    expect(badge.textContent).not.toContain('usageKind');
  });
});
