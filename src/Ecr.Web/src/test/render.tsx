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
 * ⛔ ПРИЧИНА ІСНУВАННЯ — вимiряна, не стильова. Mantine `Combobox` (і все, що
 * на ньому: `Select`, `MultiSelect`, `Autocomplete`, `TagsInput`) за
 * замовчуванням має `keepMounted: true`, тобто тримає випадний блок
 * змонтованим НАВІТЬ коли поле закрите. Позиціонування змонтованого
 * floating-елемента коштує в jsdom **~40 секунд на кожен такий компонент**.
 *
 * Виміряно 2026-09-17 (проби на чистому Mantine, без коду цього проєкту):
 *
 * | що рендериться                | час файлу |
 * |-------------------------------|-----------|
 * | лише `TextInput`              |   2.96 с  |
 * | лише `Modal`                  |   3.08 с  |
 * | `Popover` ЗАКРИТИЙ            |   3.04 с  |
 * | `Popover` ВІДКРИТИЙ           |  93.05 с  |
 * | 4 × `Select` (типова форма)   | 160.33 с  |
 * | 4 × `Select`, keepMounted:false |  2.94 с |
 *
 * ⚠ Вартість НЕ залежить від обсягу даних: `Select` із порожнім набором
 * коштує стільки ж (36.55 с), скільки `Select` із 428 поясами (35.73 с).
 * Саме тому попередні три спроби (`Q-274`, `Q-298`, `Q-330`) шукали не там:
 * вони виходили з того, що «рендер Mantine у jsdom іде понад дві хвилини» і
 * що винен великий перелік поясів. Обидва твердження хибні — Mantine
 * рендериться за мілісекунди, дорогий рівно відкритий floating-елемент.
 * Наслідком був не фікс, а чотири підняття таймаутів
 * (400_000 → 450_000 → 700_000 → 1_050_000 мс) і гейт `client`, що падав
 * випадково щоразу, коли набір тестів виростав.
 *
 * ⛔ Налаштування живе ТІЛЬКИ в тестах. У браузері `keepMounted: true` —
 * свідомий дефолт Mantine (зберігає позицію прокрутки і стан пошуку в
 * списку), і міняти поведінку продукту заради швидкості тестів не можна.
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
