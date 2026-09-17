import type { JSX, ReactNode } from 'react';
import {
  MantineProvider,
  createTheme,
  mergeThemeOverrides,
  type MantineThemeOverride,
} from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderOptions, type RenderResult } from '@testing-library/react';

/**
 * Спільна обгортка рендеру для тестів.
 *
 * ⛔ ПОПЕРЕДНЄ ПОЯСНЕННЯ, ЩО СТОЯЛО ТУТ, — СПРОСТОВАНЕ. Воно стверджувало, що
 * «позиціонування змонтованого floating-елемента коштує в jsdom ~40 секунд на
 * компонент». Заміри, на які воно спиралося, були справжні, а от МЕХАНІЗМ
 * названий неправильно: floating-елемент не позиціонується 40 секунд і
 * взагалі не бере на себе цього часу. Профіль (`node:inspector`, у самому
 * тесті) показав, що весь час іде у взаємну рекурсію jsdom ↔ `nwsapi` на
 * станових псевдокласах — `tabbable` усередині пастки фокуса питає `:modal`,
 * `nwsapi` перепитує «нативну» реалізацію, а нею в jsdom є він сам. Один
 * рендер давав 112 915 554 перевірки `:fullscreen` і 33.9 с ЧИСТОГО CPU.
 * Повний розбір і числа — у `src/test/setup.ts`, де рекурсія й обривається.
 *
 * ⚠ `keepMounted` був ТРИГЕРОМ, а не ціною: змонтований випадний блок вносив
 * у дерево пастку фокуса, і саме вона починала ланцюг. Тому й вартість не
 * залежала від обсягу даних (`Select` із порожнім набором коштував стільки ж,
 * скільки з 428 поясами) — обсяг тут ні до чого.
 *
 * ⛔ ОТЖЕ ЦЯ ОБГОРТКА БІЛЬШЕ НЕ Є МЕХАНІЗМОМ ШВИДКОСТІ. Після заглушки в
 * `setup.ts` повний набір іде 95 с проти 278–335 с, і `keepMounted` на цьому
 * вже не позначається. Обгортка лишається з іншої, меншої причини: без неї
 * вміст закритого списку присутній у DOM, тож `getByText('Альфа')` знаходить
 * приховану опцію й тест перевіряє не те, що написано. Чи прибирати її
 * зовсім — окреме рішення з власним обсягом правок, не частина цього фіксу.
 *
 * ⚠ Що з цього ПЕРЕВІРЕНО заміром: час повного набору до і після, профіль
 * рекурсії, лічильник перевірок `:fullscreen`. Що НЕ перевірялося: чи стане
 * набір ще швидшим без `keepMounted: false`. Не міряв — не тверджу.
 *
 * ⛔ Налаштування живе ТІЛЬКИ в тестах. У браузері `keepMounted: true` —
 * свідомий дефолт Mantine (зберігає позицію прокрутки і стан пошуку в
 * списку), і міняти поведінку продукту заради тестів не можна.
 */
export const testTheme = createTheme({
  components: {
    Select: { defaultProps: { comboboxProps: { keepMounted: false } } },
    MultiSelect: { defaultProps: { comboboxProps: { keepMounted: false } } },
    Autocomplete: { defaultProps: { comboboxProps: { keepMounted: false } } },
    TagsInput: { defaultProps: { comboboxProps: { keepMounted: false } } },
  },
});

/**
 * Додає дефолти тестів до вже наявної теми.
 *
 * ⚠ Для тестів, що навмисно монтують СПРАВЖНЮ тему застосунку (перевіряють
 * токени, відступи, контраст): підмінити її на `testTheme` означало б
 * перевіряти не те, що в продукті. Тут теми зливаються, тож перевірка
 * лишається на справжній темі, а `keepMounted` усе одно знімається.
 */
export function withTestDefaults(theme: MantineThemeOverride): MantineThemeOverride {
  return mergeThemeOverrides(theme, testTheme);
}

/** Обгортка без клієнта запитів — для компонентів, що не ходять у мережу. */
export function TestProviders({ children }: { children: ReactNode }): JSX.Element {
  return <MantineProvider theme={testTheme}>{children}</MantineProvider>;
}

/**
 * Обгортка з власним `QueryClient`.
 *
 * ⚠ Клієнт створюється НА КОЖЕН виклик: спільний між тестами кеш означав би,
 * що результат одного тесту протікає в наступний. `retry: false` — щоб
 * невдалий запит не чекав повторів і не з'їдав таймаут тесту.
 */
export function TestProvidersWithQuery({ children }: { children: ReactNode }): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MantineProvider>
  );
}

/** `render()` із темою тестів. */
export function renderWithMantine(
  ui: ReactNode,
  options?: Omit<RenderOptions, 'wrapper'>,
): RenderResult {
  return render(ui, { wrapper: TestProviders, ...options });
}

/** `render()` із темою тестів і клієнтом запитів. */
export function renderWithQuery(
  ui: ReactNode,
  options?: Omit<RenderOptions, 'wrapper'>,
): RenderResult {
  return render(ui, { wrapper: TestProvidersWithQuery, ...options });
}
