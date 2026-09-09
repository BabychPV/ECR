import { describe, it, expect, vi, afterEach } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useEffect, type JSX } from 'react';
import { LanguageSwitcher } from '@/shared/ui/LanguageSwitcher';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { loadCatalog, setLanguage, t } from '@/shared/i18n';

/**
 * Перемикач мови (T9 директиви №11, `ФВ-14.9`).
 *
 * ⛔ Мов у розкривному списку РІВНО ТРИ — `en`/`ru`/`kz`. Реєстр
 * (`sys_ecr.Language`) уже давно має саме ці три (`09-seed.sql`), і `uk` тут
 * НЕ додається: це попереднє, вже узгоджене рішення проєкту, а не пропуск.
 *
 * ⚠ Переклади `ru`/`kz` для ~450 рядків — робота термінолога (`C-7`), не
 * цього перемикача. Фікстури нижче симулюють гіпотетичний перекладений
 * рядок, щоб довести, що перемикання ФАКТИЧНО тягне і показує чужий каталог,
 * а не лише запам'ятовує код мови, — це фікстура відповіді сервера для
 * тесту, не вигадана клієнтом локалізація продукту.
 */
function routes(map: Record<string, unknown>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();

      for (const [needle, body] of Object.entries(map)) {
        if (url.includes(needle)) {
          return new Response(JSON.stringify(body), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }
      }

      throw new Error(`Тест не очікував запиту: ${url}`);
    }),
  );
}

const Languages = [
  { code: 'en', nameNative: 'English', isDefault: true },
  { code: 'ru', nameNative: 'Русский', isDefault: false },
  { code: 'kz', nameNative: 'Қазақша', isDefault: false },
];

/** Сторінка, що підписана на каталог так само, як `AppLayout`/`LoginPage`. */
function Page(): JSX.Element {
  useCatalog();

  useEffect(() => {
    // Так само, як `AppLayout` тягне приватний каталог одразу після входу.
    void loadCatalog('en', 'private');
  }, []);

  return (
    <>
      <LanguageSwitcher />
      <p>{t('nav.documents')}</p>
      {/* Маркер, а не сам напис: дає тесту дочекатися завершення
          мережевого `loadCatalog`, навіть коли видимий напис не міняється
          (наприклад, тому що переклад підмінено англійським). */}
      <p>{t('test.marker')}</p>
    </>
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <Page />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
  // ⚠ Каталог живе в модулі й переживає тест (як у `catalog.test.tsx`):
  // повертаємо мову за замовчуванням, щоб один тест не впливав на інший.
  setLanguage('en');
});

describe('Перемикач мови інтерфейсу', () => {
  it('показує мови з реєстру, а не вшиту в збірку трійку', async () => {
    routes({
      '/api/v1/languages': Languages,
      '/api/v1/ui-strings/en': { languageCode: 'en', revision: 1, strings: {} },
    });

    show();

    const select = (await screen.findByLabelText('⟦profile.language⟧')) as HTMLSelectElement;
    const labels = Array.from(select.options).map((option) => option.textContent);

    expect(labels).toEqual(['English', 'Русский', 'Қазақша']);
    expect(labels).not.toContain('Українська');
  });

  it('ФВ-14.9/T9: перемикання мови показує каталог іншої мови, не лишається на старому', async () => {
    routes({
      '/api/v1/languages': Languages,
      '/api/v1/ui-strings/en': {
        languageCode: 'en',
        revision: 1,
        strings: { 'nav.documents': 'Documents', 'test.marker': 'en-loaded' },
      },
      '/api/v1/ui-strings/ru': {
        languageCode: 'ru',
        revision: 1,

        // ⚠ Фікстура тесту, не переклад продукту (`C-7`): доводить лише, що
        // перемикач ФАКТИЧНО тягне чужий каталог, а не просто запам'ятовує
        // код мови.
        strings: { 'nav.documents': 'Документы (тест)', 'test.marker': 'ru-loaded' },
      },
    });

    show();

    expect(await screen.findByText('en-loaded')).toBeDefined();
    expect(screen.getByText('Documents')).toBeDefined();

    const select = await screen.findByLabelText('⟦profile.language⟧');
    fireEvent.change(select, { target: { value: 'ru' } });

    // Маркер підтверджує, що `ru`-каталог доїхав, перш ніж перевіряти напис.
    expect(await screen.findByText('ru-loaded')).toBeDefined();

    // ⛔ D-134: якби перемикач лише кликав `setLanguage` і НЕ тягнув каталог
    // обраної мови (`loadCatalog`), `t()` і далі читав би єдиний завантажений
    // каталог (`en`) — напис лишався б «Documents» назавжди, хоч би скільки
    // мов з'явилося в реєстрі з реальними перекладами.
    expect(screen.getByText('Документы (тест)')).toBeDefined();
    expect(screen.queryByText('Documents')).toBeNull();
  });

  it("запам'ятовує обрану мову для наступного відкриття (сторінка входу читає той самий ключ)", async () => {
    routes({
      '/api/v1/languages': Languages,
      '/api/v1/ui-strings/en': {
        languageCode: 'en',
        revision: 1,
        strings: { 'nav.documents': 'Documents', 'test.marker': 'en-loaded' },
      },
      '/api/v1/ui-strings/kz': {
        languageCode: 'kz',
        revision: 1,
        strings: { 'test.marker': 'kz-loaded' },
      },
    });

    show();

    expect(await screen.findByText('en-loaded')).toBeDefined();

    const select = await screen.findByLabelText('⟦profile.language⟧');
    fireEvent.change(select, { target: { value: 'kz' } });

    // Дає мережевому `loadCatalog` усередині обробника завершитися.
    expect(await screen.findByText('kz-loaded')).toBeDefined();

    // ⛔ D-134: якби перемикач НЕ кликав `setLanguage` (лише `loadCatalog`),
    // вибір ніколи не потрапляв би в `localStorage`, і `preferredLanguage()`
    // на сторінці входу після виходу знову показував би англійську, скільки
    // разів людина не перемикала б мову в застосунку.
    expect(localStorage.getItem('uiLanguage')).toBe('kz');
  });

  it('відсутність перекладу в обраній мові показує англійський оригінал, не порожнечу й не ключ', async () => {
    routes({
      '/api/v1/languages': Languages,
      '/api/v1/ui-strings/en': {
        languageCode: 'en',
        revision: 1,
        strings: { 'nav.documents': 'Documents', 'test.marker': 'en-loaded' },
      },

      // ⚠ Композиція «оригінал + переклад» — на СЕРВЕРІ
      // (`UiStringResolver.Compose`): відповідь на запит `ru`-каталогу вже
      // містить англійське значення там, де перекладу немає. Це і є та
      // поведінка, яку мав перевірити T9, а не вигадати.
      '/api/v1/ui-strings/ru': {
        languageCode: 'ru',
        revision: 1,
        strings: { 'nav.documents': 'Documents', 'test.marker': 'ru-loaded' },
      },
    });

    show();

    expect(await screen.findByText('en-loaded')).toBeDefined();

    const select = await screen.findByLabelText('⟦profile.language⟧');
    fireEvent.change(select, { target: { value: 'ru' } });

    expect(await screen.findByText('ru-loaded')).toBeDefined();

    // Той самий напис — англійською, під мовою `ru` — а не `⟦nav.documents⟧`.
    expect(screen.getByText('Documents')).toBeDefined();
    expect(screen.queryByText('⟦nav.documents⟧')).toBeNull();
  });
});
