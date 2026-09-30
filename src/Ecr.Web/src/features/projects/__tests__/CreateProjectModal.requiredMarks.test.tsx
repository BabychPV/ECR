import { afterEach, describe, expect, it, vi } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateProjectModal, ProjectFieldLabelKey } from '@/features/projects/CreateProjectModal';
import { testTheme } from '@/test/render';

/**
 * U-13 (UX-PASS 2026-09-23): зірочку мало одне обов'язкове поле з п'яти.
 *
 * ⛔ Рядок «Still needed: Code, Name, Site time zone (IANA), Template version,
 * Period policy» перелічував п'ять полів, а `*` стояла лише біля поясу. Тобто
 * форма казала одне позначкою і інше текстом.
 *
 * ⛔ Перелік очікуваних полів береться з ТОГО САМОГО джерела, що й рядок
 * «Still needed» (`ProjectFieldLabelKey`), а не переписаний тут руками. Інакше
 * нове обов'язкове поле, додане до переліку бракуючих без зірочки, лишило б
 * тест зеленим — рівно та розбіжність, яку він стереже.
 *
 * ⚠ `customPeriodCount` виключено: поле видиме лише для `Custom`, а форма
 * відкривається з `Monthly`. Воно й до цього фіксу мало `required`.
 *
 * ⚠ Перевіряються ОБИДВІ ознаки: видима зірочка в підписі (її бачить людина)
 * і атрибут `required` на полі (його чує читалка, `aria-required`). Друге без
 * першого — те саме U-13 для зрячого, перше без другого — для незрячого.
 *
 * Мутаційно перевірено: прибрати `required` з поля «Code» у
 * `CreateProjectModal.tsx` → червоний (`periods.code`); прибрати
 * `required && language.isDefault` у `LocalizedInput.tsx` → червоний
 * (`periods.name`).
 */
function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.includes('/api/v1/languages')
        ? [{ code: 'en', nameNative: 'English', isDefault: true }]
        : url.includes('/api/v1/templates')
          ? { items: [], nextCursor: null, totalCount: 0 }
          : url.includes('/api/v1/projects/period-policies')
            ? []
            : null;

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Підписи полів, у яких є зірочка і чиє поле несе `required`, — ключами каталогу. */
function requiredLabelKeys(): string[] {
  return Array.from(document.querySelectorAll('label'))
    .filter((label) => {
      const star = label.querySelector('.mantine-InputWrapper-required');
      const input = label.htmlFor === '' ? null : document.getElementById(label.htmlFor);

      return star !== null && input instanceof HTMLInputElement && input.required;
    })
    .map((label) => /⟦([^⟧]+)⟧/.exec(label.textContent ?? '')?.[1] ?? '');
}

describe('CreateProjectModal: кожне поле з «Still needed» позначене обов\'язковим (U-13)', () => {
  it(
    'зірочку і required мають усі поля, які може назвати рядок бракуючих',
    async () => {
      respond();
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

      const rendered = render(
        <MantineProvider theme={testTheme}>
          <QueryClientProvider client={client}>
            <CreateProjectModal opened onClose={() => {}} onCreated={async () => {}} />
          </QueryClientProvider>
        </MantineProvider>,
      );

      // Поле назви з'являється лише після приїзду реєстру мов.
      await vi.waitFor(
        () => {
          expect(document.querySelector('[data-localized-input="pending"]')).toBeNull();
        },
        { timeout: 60_000 },
      );

      const marked = requiredLabelKeys();
      const expected = Object.entries(ProjectFieldLabelKey)
        .filter(([field]) => field !== 'customPeriodCount')
        .map(([, key]) => key);

      for (const key of expected) {
        expect(marked, key).toContain(key);
      }

      rendered.unmount();
    },
    200_000,
  );
});
