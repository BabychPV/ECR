import { afterEach, describe, expect, it, vi } from 'vitest';
import { isCatalogResolved, language, loadCatalog, subscribeCatalog } from '@/shared/i18n';

/**
 * Живий дефект: перший реальний прогін застосунку (не мова за замовчуванням
 * `'en'`, а мова профілю — `AppLayout`, `me.language`) сипав у консоль
 * десятки `console.error("Немає рядка інтерфейсу: nav.*")` на КОЖНЕ
 * відкриття, хоча текст на екрані вже за мить показувався правильно.
 *
 * ⛔ Причина — той самий клас дефекту, що `Q-305` (Breadcrumbs): `bumpCatalog()`
 * викликає підписників (`useSyncExternalStore`) СИНХРОННО, без пакетування
 * React. `loadCatalog` для мови, відмінної від активної, робив ДВА окремих
 * виклики — один одразу після `loaded.set(...)` (вміст уже новий), другий
 * після `current = lang` (активна мова вже нова). React примусово
 * перемальовує дерево між ними: перший рендер бачить `isCatalogResolved`
 * (з `loaded`) істинним, а `language()` — ще СТАРИМ. Компонент, що чекає
 * саме на це («каталог для моєї мови готовий — можна `t()`»), читає
 * застарілу активну мову й не знаходить у ній щойно завантажений ключ.
 *
 * ⚠ Тест не рендерить React: `subscribeCatalog`/`isCatalogResolved`/
 * `language()` — той самий контракт, яким користується `useCatalog()`
 * (`useSyncExternalStore`), і перевіряється рівно те, що бачить підписник
 * при КОЖНОМУ сповіщенні.
 */

function ok(languageCode: string): Response {
  return new Response(JSON.stringify({ languageCode, revision: 1, strings: { probe: 'ok' } }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ETag: `"private-${languageCode}-1"` },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('loadCatalog: вміст і активна мова оновлюються одним сповіщенням', () => {
  it('жодне сповіщення не показує готовий каталог нової мови разом зі старою активною мовою', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/ui-strings/uk')) return Promise.resolve(ok('uk'));
        throw new Error(`неочікуваний запит у тесті: ${url}`);
      }),
    );

    const observations: { resolved: boolean; current: string }[] = [];
    const unsubscribe = subscribeCatalog(() => {
      observations.push({ resolved: isCatalogResolved('uk', 'private'), current: language() });
    });

    try {
      // ⚠ Активна мова стартує з `'en'` (дефолт модуля) і НЕ змінюється
      // синхронно тут — так само, як `AppLayout` кличе `loadCatalog(me.language, ...)`
      // без попереднього `setLanguage`.
      await loadCatalog('uk', 'private');
    } finally {
      unsubscribe();
    }

    expect(observations.length).toBeGreaterThan(0);

    // ⛔ Це і є доказ дефекту: підписник (а отже, і React-компонент за ним)
    // ніколи не повинен бачити «каталог uk готовий» разом із мовою, що ще не
    // 'uk' — саме ця комбінація давала `t()` порожній результат для щойно
    // завантаженої мови.
    const inconsistent = observations.filter((o) => o.resolved && o.current !== 'uk');
    expect(inconsistent).toEqual([]);

    // І фінальний стан справді узгоджений.
    expect(language()).toBe('uk');
  });

  it(
    'те саме, коли каталог УЖЕ є в localStorage від попереднього завантаження ' +
      '(шлях, що й відтворював живий дефект — не мережевий, а кешовий)',
    async () => {
      // ⛔ Саме цей шлях і давав живий дефект на кожному відкритті застосунку:
      // `AppLayout` кличе `loadCatalog(me.language, 'private')` для мови
      // ПРОФІЛЮ (сервер), а не поточної `current`; до першого в сесії виклику
      // збережений каталог із МИНУЛОЇ сесії вже лежить у `localStorage`.
      // Підстановка збереженого значення (нижче) відбувається СИНХРОННО, до
      // будь-якого `await` й до мережевого запиту, — і саме тому виправлення
      // лише мережевого шляху (перший тест) не рятувало живий випадок.
      // ⚠ Інша мова, ніж у першому тесті ('kz', не 'uk'): `loaded` — мапа на
      // рівні МОДУЛЯ, і якби тут теж стояло 'uk', умова `!loaded.has(cacheKey)`
      // була б хибною ще ДО цього тесту (перший тест уже завантажив 'uk') —
      // кешовий шлях узагалі не спрацював би, і тест мовчки перевіряв би не
      // те, що заявлено.
      localStorage.setItem('uiStrings:kz:private:revision', '1');
      localStorage.setItem(
        'uiStrings:kz:private:1',
        JSON.stringify({ languageCode: 'kz', revision: 1, strings: { probe: 'cached' } }),
      );

      vi.stubGlobal(
        'fetch',
        vi.fn((input: RequestInfo | URL) => {
          const url = String(input);
          if (url.includes('/ui-strings/kz')) return Promise.resolve(ok('kz'));
          throw new Error(`неочікуваний запит у тесті: ${url}`);
        }),
      );

      const observations: { resolved: boolean; current: string }[] = [];
      const unsubscribe = subscribeCatalog(() => {
        observations.push({ resolved: isCatalogResolved('kz', 'private'), current: language() });
      });

      try {
        await loadCatalog('kz', 'private');
      } finally {
        unsubscribe();
      }

      // ⚠ Кешоване значення саме тут і мало б дати ПЕРШЕ (найшвидше)
      // сповіщення — задовго до відповіді мережі.
      expect(observations.length).toBeGreaterThan(0);

      const inconsistent = observations.filter((o) => o.resolved && o.current !== 'kz');
      expect(inconsistent).toEqual([]);
      expect(language()).toBe('kz');
    },
  );
});
