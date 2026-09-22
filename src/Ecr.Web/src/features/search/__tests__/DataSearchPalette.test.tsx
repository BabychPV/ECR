import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { SearchLauncher } from '@/features/search/SearchLauncher';

/**
 * Командна палітра з пошуком даних (BE-19): стани, групи, клавіатура, фокус.
 *
 * ⚠ Монтується через справжній `SearchLauncher` — тобто разом із лінивим
 * `import()` палітри, а не в обхід нього.
 *
 * ⚠ Очікувані написи беруться тим самим `t()`, що й у компоненті: каталогу
 * в тесті немає, тож обидві сторони бачать `⟦ключ⟧`, і тест не залежить від
 * перекладу.
 *
 * ⛔ Модуль палітри прогрівається в `beforeAll`. Без цього ПЕРШИЙ тест файлу
 * платив за холодний `import()` усього графа палітри (`Modal`, `Combobox`,
 * `useRateLimitedSearch`…) усередині типової 1 с `findByRole('combobox')`.
 * Заміряно (2026-09-21): холодний імпорт — 180–260 мс у спокої й 1608 мс під
 * навантаженням, тобто довше за весь бюджет `findBy*`; повний `npm test` під
 * навантаженням падав тут 3 рази з 8, завжди на першому тесті й завжди
 * «Unable to find role="combobox"». Прогрів не обходить лінивість: палітра
 * й далі монтується через `lazy()` + `Suspense` справжнього `SearchLauncher`,
 * просто модуль уже обчислено. Те, що до першого відкриття він НЕ
 * обчислюється, стереже окремий `SearchLauncher.lazy.test.tsx`.
 */

beforeAll(async () => {
  await import('@/features/search/DataSearchPalette');
});

const original = globalThis.fetch;

afterEach(() => {
  cleanup();
  globalThis.fetch = original;
});

type Reply = () => Response | Promise<Response>;

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Підміняє мережу; повертає запити до `/api/v1/search` у порядку надходження. */
function serve(reply: Reply): { terms: () => string[] } {
  const terms: string[] = [];

  globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
    const url = new URL(String(input), 'http://x');
    if (url.pathname === '/api/v1/search') terms.push(url.searchParams.get('q') ?? '');

    return reply();
  }) as typeof globalThis.fetch;

  return { terms: () => terms };
}

function Location(): JSX.Element {
  return <span data-testid="location">{useLocation().pathname}</span>;
}

function mount(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/units']}>
          <SearchLauncher />
          <Location />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openPalette(): Promise<{ user: ReturnType<typeof userEvent.setup>; input: HTMLElement }> {
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: t('search.open') }));
  const input = await screen.findByRole('combobox', { name: t('search.open') });
  await waitFor(() => expect(document.activeElement).toBe(input));

  return { user, input };
}

const Hits = [
  { kind: 'registry', id: 3, code: 'FUEL', title: 'Fuel types' },
  { kind: 'document', id: 42, code: 'DOC-42', title: 'Permit 2026' },
  { kind: 'template', id: 7, code: 'T-7', title: 'Emissions' },
  { kind: 'document', id: 43, code: 'DOC-43', title: 'Permit 2025' },
];

describe('палітра: пошук даних', () => {
  it('коротший за 2 символи запит — підказка, і в мережу нічого не йде', async () => {
    const net = serve(() => json(Hits));
    mount();
    const { user, input } = await openPalette();

    await user.type(input, 'a');

    expect(screen.getByRole('status').textContent).toBe(t('search.minLength', { min: 2 }));
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(net.terms()).toEqual([]);
  });

  it('швидке введення дає ОДИН запит — останнім значенням (debounce)', async () => {
    const net = serve(() => json(Hits));
    mount();
    const { user, input } = await openPalette();

    await user.type(input, 'Permit');

    expect(await screen.findByRole('listbox')).toBeTruthy();
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(net.terms()).toEqual(['Permit']);
  });

  it('поки відповіді немає — стан завантаження', async () => {
    let release: (value: Response) => void = () => undefined;
    const net = serve(() => new Promise<Response>((resolve) => (release = resolve)));
    mount();
    const { user, input } = await openPalette();

    await user.type(input, 'Pe');

    // Запит уже в мережі, відповіді ще немає.
    await waitFor(() => expect(net.terms()).toEqual(['Pe']));
    expect(screen.getByRole('status').textContent).toBe(t('common.loading'));
    release(json([]));
    await waitFor(() => expect(screen.getByRole('status').textContent).toBe(t('search.empty')));
  });

  it('порожня відповідь — «нічого не знайдено»', async () => {
    serve(() => json([]));
    mount();
    const { user, input } = await openPalette();

    await user.type(input, 'zzz');

    await waitFor(() => expect(screen.getByRole('status').textContent).toBe(t('search.empty')));
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('відмова сервера — помилка, а НЕ «нічого не знайдено»', async () => {
    serve(() => new Response('bad gateway', { status: 502 }));
    mount();
    const { user, input } = await openPalette();

    await user.type(input, 'Permit');

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByRole('status').textContent).not.toContain(t('search.empty'));
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('збіги згруповані за видом у сталому порядку; назва як є, код поруч', async () => {
    serve(() => json(Hits));
    mount();
    const { user, input } = await openPalette();

    await user.type(input, 'Pe');

    const list = await screen.findByRole('listbox');
    const groups = within(list).getAllByRole('group');
    expect(groups.map((group) => group.getAttribute('aria-labelledby') ?? '')).toHaveLength(3);
    expect(groups.map((group) => within(group).getAllByRole('option').length)).toEqual([2, 1, 1]);
    expect(
      screen.getByRole('group', { name: t('nav.documents') }).contains(
        screen.getByRole('option', { name: /Permit 2026/ }),
      ),
    ).toBe(true);
    expect(screen.getByRole('group', { name: t('nav.registries') })).toBeTruthy();

    const option = screen.getByRole('option', { name: /Emissions/ });
    expect(option.querySelector('[data-code-text]')?.textContent).toBe('T-7');
  });

  it('стрілки ведуть курсор, Enter відкриває обраний збіг', async () => {
    serve(() => json(Hits));
    mount();
    const { user, input } = await openPalette();

    await user.type(input, 'Pe');
    await screen.findByRole('listbox');

    const first = screen.getByRole('option', { name: /Permit 2026/ });
    expect(first.getAttribute('aria-selected')).toBe('true');
    expect(input.getAttribute('aria-activedescendant')).toBe(first.id);

    // Вгору з першого — на останній (довідник), вниз з останнього — на перший.
    await user.keyboard('{ArrowUp}');
    expect(screen.getByRole('option', { name: /Fuel types/ }).getAttribute('aria-selected')).toBe(
      'true',
    );
    await user.keyboard('{ArrowDown}');
    expect(first.getAttribute('aria-selected')).toBe('true');

    // Документ, документ → шаблон.
    await user.keyboard('{ArrowDown}{ArrowDown}');
    const template = screen.getByRole('option', { name: /Emissions/ });
    expect(template.getAttribute('aria-selected')).toBe('true');
    expect(input.getAttribute('aria-activedescendant')).toBe(template.id);

    await user.keyboard('{Enter}');

    await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/admin/templates/7'));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('клік по довіднику веде в його конструктор', async () => {
    serve(() => json(Hits));
    mount();
    const { user, input } = await openPalette();

    await user.type(input, 'Fu');
    await user.click(await screen.findByRole('option', { name: /Fuel types/ }));

    await waitFor(() =>
      expect(screen.getByTestId('location').textContent).toBe('/admin/registries/FUEL/definition'),
    );
  });

  it('Escape закриває палітру й повертає фокус на кнопку пошуку', async () => {
    serve(() => json(Hits));
    mount();
    const { user } = await openPalette();
    const button = screen.getByRole('button', { name: t('search.open'), hidden: true });

    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(button));
  });
});
