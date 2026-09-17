import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { isCatalogResolved, loadCatalog, setLanguage, subscribeCatalog } from '@/shared/i18n';

/**
 * Атрибут `lang` кореневого `<html>` мусить слідувати за активною мовою
 * інтерфейсу.
 *
 * ⛔ Живий дефект: `index.html` оголошував мову документа СТАТИЧНО і більше її
 * ніхто не чіпав. Застосунок підтримує три мови (`en`/`ru`/`kz` у реєстрі
 * `sys_ecr.Language`), користувач перемикає їх на ходу — а документ до кінця
 * сеансу лишався тією мовою, що стояла в розмітці збірки.
 *
 * Ціна не косметична. Читалка обирає правила вимови САМЕ з `lang`: російський
 * чи казахський інтерфейс, озвучений англійською фонетикою, — не «трохи не
 * так», а нечитний для тих, хто інакше працювати не може. Той самий атрибут
 * керує переносами, лапками й підстановкою шрифту в браузері.
 *
 * ⚠ Тест не рендерить React навмисно: `<html lang>` пише сам шар i18n
 * (`bumpCatalog`), а не компонент, — отже перевіряти треба контракт модуля,
 * а не одну конкретну оболонку. Компонентний тест довів би лише, що ця
 * оболонка змонтована, і мовчав би про `LoginPage`/`KitchenSinkPage`, які
 * показують текст поза `AppLayout`.
 */

function ok(languageCode: string): Response {
  return new Response(JSON.stringify({ languageCode, revision: 1, strings: { probe: 'ok' } }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ETag: `"public-${languageCode}-1"` },
  });
}

beforeEach(() => {
  // ⚠ Свідомо НЕ та мова, що її очікує будь-яке з тверджень нижче: інакше
  // тест був би зелений і на коді, який атрибут не чіпає взагалі.
  document.documentElement.lang = 'xx-initial';
});

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('<html lang> слідує за активною мовою інтерфейсу', () => {
  it('перемикання мови користувачем оновлює lang документа', () => {
    setLanguage('ru');

    expect(document.documentElement.lang).toBe('ru');
  });

  it("внутрішній код 'kz' стає тегом BCP-47 'kk' (а не залишається кодом країни)", () => {
    // ⛔ `kz` — код КРАЇНИ Казахстан (ISO 3166), а не мови. Тег мови для
    // казахської — `kk` (ISO 639-1). `lang="kz"` для читалки означає
    // невідому мову: вона мовчки відкочується до мови системи, тобто
    // повертає рівно ту проблему, заради якої атрибут і оновлюють.
    setLanguage('kz');

    expect(document.documentElement.lang).toBe('kk');
  });

  it("код без розбіжності ('en') проходить як є", () => {
    setLanguage('en');

    expect(document.documentElement.lang).toBe('en');
  });

  it('перше завантаження відновленою мовою (без setLanguage) теж оновлює lang', async () => {
    // ⛔ Саме цей шлях, а не перемикач: `LoginPage` кличе
    // `loadCatalog(preferredLanguage(), 'public')`, а `AppLayout` —
    // `loadCatalog(me.language, 'private')` мовою ПРОФІЛЮ. Жоден із них
    // `setLanguage` не викликає: активну мову змінює сам `loadCatalog`.
    // Виправлення, прив'язане лише до перемикача, лишило б перше відкриття
    // застосунку з мовою розмітки збірки — тобто найчастіший випадок.
    document.documentElement.lang = 'xx-initial';

    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/ui-strings/ru')) return Promise.resolve(ok('ru'));
        throw new Error(`неочікуваний запит у тесті: ${url}`);
      }),
    );

    await loadCatalog('ru', 'public');

    expect(document.documentElement.lang).toBe('ru');
  });

  it('відновлена мова з кешу localStorage оновлює lang ще до відповіді мережі', async () => {
    // ⚠ Кешовий шлях `loadCatalog` спрацьовує СИНХРОННО, до першого `await`,
    // і саме він виконується на кожному повторному відкритті застосунку.
    // Мова 'kz' тут ще й перевіряє, що переклад у BCP-47 застосовується на
    // ВСІХ шляхах зміни активної мови, а не лише в `setLanguage`.
    localStorage.setItem('uiStrings:kz:public:revision', '1');
    localStorage.setItem(
      'uiStrings:kz:public:1',
      JSON.stringify({ languageCode: 'kz', revision: 1, strings: { probe: 'cached' } }),
    );

    document.documentElement.lang = 'xx-initial';

    vi.stubGlobal(
      'fetch',
      vi.fn(
        () =>
          new Promise<Response>(() => {
            // Мережа навмисно НІКОЛИ не відповідає: твердження нижче має
            // виконатися з самого лише кешу.
          }),
      ),
    );

    void loadCatalog('kz', 'public');

    expect(document.documentElement.lang).toBe('kk');
  });

  it('жодне сповіщення підписників не показує готовий каталог зі старим lang', async () => {
    // ⛔ Та сама вимога атомарності, що й `Q-305`: `bumpCatalog()` кличе
    // підписників (`useSyncExternalStore`) СИНХРОННО, без пакетування React.
    // Якби `<html lang>` писався окремим ефектом ПІСЛЯ рендера, існував би
    // кадр, у якому каталог нової мови вже готовий і показаний, а документ
    // усе ще оголошений старою мовою — читалка озвучила б саме цей кадр.
    document.documentElement.lang = 'xx-initial';

    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/ui-strings/ru')) return Promise.resolve(ok('ru'));
        throw new Error(`неочікуваний запит у тесті: ${url}`);
      }),
    );

    const observations: { resolved: boolean; documentLang: string }[] = [];
    const unsubscribe = subscribeCatalog(() => {
      observations.push({
        resolved: isCatalogResolved('ru', 'private'),
        documentLang: document.documentElement.lang,
      });
    });

    try {
      await loadCatalog('ru', 'private');
    } finally {
      unsubscribe();
    }

    expect(observations.length).toBeGreaterThan(0);

    const inconsistent = observations.filter((o) => o.resolved && o.documentLang !== 'ru');
    expect(inconsistent).toEqual([]);
  });
});
