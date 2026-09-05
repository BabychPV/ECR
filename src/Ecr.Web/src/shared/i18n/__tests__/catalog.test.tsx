import { describe, it, expect, vi, afterEach } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import type { JSX } from 'react';
import { catalogSnapshot, loadCatalog, t } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';

/**
 * Каталог рядків доїжджає ПІСЛЯ першого рендера.
 *
 * ⛔ Це не гіпотетичний сценарій, а те, як працює кожне відкриття сторінки:
 * `loadCatalog` — мережевий запит, `t()` викликається до його завершення.
 * Дефект був живий і видимий кожному: сторінка входу показувала `login.title`
 * замість назви системи, доки користувач не натискав клавішу в полі — тоді
 * `useState` давав перерендер, і написи «раптом» з'являлися.
 *
 * ⚠ Тест перевіряє саме ВІДСУТНІСТЬ взаємодії: жодного кліку, жодного набору.
 * Тест, який спершу щось натискає, зелений і на зламаному коді.
 */
function catalog(strings: Record<string, string>, revision = 7): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'en', revision, strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: `"public-en-${revision}"` },
        }),
      ),
    ),
  );
}

/** Підписаний компонент — так, як це робить `LoginPage`. */
function Subscribed({ k }: { k: string }): JSX.Element {
  useCatalog();

  return <h1>{t(k)}</h1>;
}

/** Непідписаний — так, як було до виправлення. */
function Unsubscribed({ k }: { k: string }): JSX.Element {
  return <h1>{t(k)}</h1>;
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('Каталог рядків інтерфейсу', () => {
  it('показує напис, щойно каталог завантажено, без жодної взаємодії', async () => {
    catalog({ 'login.title': 'Environmental Compliance Reporting' });

    render(<Subscribed k="login.title" />);
    // ⚠ У кутових дужках (`D-138`): голий ключ на екрані виглядає майже
    // правдоподібно, і саме тому `A7-33` прожила так довго.
    expect(screen.getByRole('heading').textContent).toBe('⟦login.title⟧');

    await act(async () => {
      await loadCatalog('en', 'public');
    });

    expect(screen.getByRole('heading').textContent).toBe('Environmental Compliance Reporting');
  });

  it('без підписки напис лишається ключем — саме цей дефект і був живий', async () => {
    // ⚠ Ключ ІНШИЙ, ніж у попередньому тесті: каталог живе в модулі й
    // переживає тест. Той самий ключ уже був би перекладений на першому
    // рендері, і тест довів би не те, що перевіряє.
    catalog({ 'login.windows': 'Sign in with Windows' });

    render(<Unsubscribed k="login.windows" />);

    await act(async () => {
      await loadCatalog('en', 'public');
    });

    // ⚠ Негативна перевірка обов'язкова: без неї попередній тест був би
    // зеленим навіть якби React перемальовував усе сам, і сторож не сторожив би.
    expect(screen.getByRole('heading').textContent).toBe('⟦login.windows⟧');
  });

  it('ФВ-14.9c: на 304 ідентичність каталогу в сторі не змінюється', async () => {
    catalog({ 'nav.documents': 'Documents' }, 11);
    await loadCatalog('en', 'public');

    const before = catalogSnapshot();

    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(null, { status: 304 }))),
    );

    await loadCatalog('en', 'public');

    // ⛔ Версія НЕ зросла. Інакше `useSyncExternalStore` перемалював би все
    // дерево на кожному відкритті сторінки — і виграш умовного запиту зник би
    // рівно там, де він мав бути.
    expect(catalogSnapshot()).toBe(before);
    expect(t('nav.documents')).toBe('Documents');
  });

  it('другий запит іде з If-None-Match і на 304 лишає збережене', async () => {
    catalog({ 'login.submit': 'Sign in' }, 9);
    await loadCatalog('en', 'public');

    const conditional = vi.fn((_input: string, _init?: RequestInit) =>
      Promise.resolve(new Response(null, { status: 304 })),
    );
    vi.stubGlobal('fetch', conditional);

    await loadCatalog('en', 'public');

    const headers = new Headers(conditional.mock.calls[0]?.[1]?.headers);
    expect(headers.get('If-None-Match')).toBe('"public-en-9"');
    expect(t('login.submit')).toBe('Sign in');
  });
});
