import { describe, it, expect, vi, afterEach } from 'vitest';
import { language, loadCatalog, setLanguage } from '@/shared/i18n';

/**
 * `Q-255`: перемикання мови двічі поспіль не має відкочуватись до першого,
 * уже покинутого вибору, якщо мережева відповідь на нього приходить пізніше
 * за відповідь на другий (останній) вибір.
 *
 * ⛔ Сценарій дефекту: користувач клікає "kz" (`setLanguage('kz')` —
 * `current` синхронно стає `'kz'`, стартує `loadCatalog('kz', ...)`), одразу
 * виправляється й клікає "ru" (`current` стає `'ru'`, стартує
 * `loadCatalog('ru', ...)`). Мережа не гарантує порядку відповідей: якщо
 * відповідь на "kz" (покинутий вибір) приходить ПІСЛЯ відповіді на "ru"
 * (останній вибір), стара реалізація порівнювала лише `current !== lang`
 * і мовчки відкочувала `current` назад до `'kz'` — без жодної подальшої дії
 * користувача.
 *
 * ⚠ Окремий файл, а не додатковий `it` у `catalog.test.tsx`: тест навмисно
 * лишає модуль у стані з мовою `'ru'` (а не скидає його назад на `'en'`), щоб
 * перевірка була точною — і не хоче ризикувати вплинути на сусідні тести
 * того самого файлу, які читають `t()` за замовчуванням для `'en'`. Vitest
 * ізолює модульний граф між файлами тестів, тож `current` тут завжди
 * стартує з дефолтного `'en'`.
 */
function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });

  return { promise, resolve };
}

function ok(languageCode: string): Response {
  return new Response(JSON.stringify({ languageCode, revision: 1, strings: {} }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ETag: `"private-${languageCode}-1"` },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('loadCatalog: гонка двох послідовних перемикань мови (Q-255)', () => {
  it('остання мова перемагає, навіть якщо відповідь на покинутий вибір приходить пізніше', async () => {
    const kz = deferred<Response>();
    const ru = deferred<Response>();

    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/ui-strings/kz')) return kz.promise;
        if (url.includes('/ui-strings/ru')) return ru.promise;
        throw new Error(`неочікуваний запит у тесті: ${url}`);
      }),
    );

    // ⚠ Точно та сама послідовність, що й у `LanguageSwitcher.onChange`:
    // `setLanguage` синхронно міняє активну мову, `loadCatalog` іде окремо,
    // асинхронно, без очікування.
    setLanguage('kz');
    const first = loadCatalog('kz', 'private');

    setLanguage('ru');
    const second = loadCatalog('ru', 'private');

    // Відповідь на ОСТАННІЙ вибір ('ru') приходить ПЕРШОЮ.
    ru.resolve(ok('ru'));
    await second;
    expect(language()).toBe('ru');

    // Відповідь на ПОКИНУТИЙ вибір ('kz') приходить ПІЗНІШЕ.
    kz.resolve(ok('kz'));
    await first;

    // ⛔ Це і є перевірка дефекту: активна мова має лишитися 'ru' — останній
    // вибір користувача, — а не відкотитися до 'kz' лише тому, що ЙОГО
    // відповідь прийшла останньою.
    expect(language()).toBe('ru');
  });
});
