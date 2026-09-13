import type { JSX } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, within, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { GrantsPanel } from '@/pages/admin/GrantsPanel';
import type { RoleView } from '@/api/types';

/**
 * Колонка розв'язаної назви ресурсу в переліку грантів (`Q-299`).
 *
 * ⛔ До цієї картки адміністратор бачив голий `resourceId` і не мав способу
 * дізнатися, якому аркушу/таблиці/колонці він відповідає. Сервер тепер
 * повертає `resourceName` (`ListResourceGrantsHandler`), а ця сторінка мусить
 * ПОКАЗАТИ його — три окремі стани, які нічим одне на одного не схожі:
 * розв'язано (рядок коду), не розв'язано (`null` — «осиротіле» посилання) і
 * ще не відомо (`undefined` — щойно додано, сервер ще не бачив).
 */

/**
 * ⛔ `Select`/`MultiSelect` (`@mantine/core`) під jsdom «зависають» —
 * відтворюваний факт, задокументований уже ДВІЧІ в цьому репозиторії
 * (`approval-route-editor.test.tsx`, `user-access-editor.test.tsx`: жоден
 * наявний тест не рендерить їх напряму саме тому). Перевірено емпірично і
 * тут: реальний клік по `Select` (навіть із поліфілом `scrollIntoView` у
 * `src/test/setup.ts`) не падає й не кидає винятку — він просто НЕ встигає
 * розкрити список опцій за розумний час (спостережено ~166с на ОДИН клік в
 * ізоляції; попередній варіант цього файлу йшов у таймаут навіть на
 * 400000мс під паралельним навантаженням повного прогону — `npm test`
 * підтвердив це: `Test timed out in 400000ms` на обох тестах). Це не баг
 * цієї картки — це та сама причина, яку вже обійшли двічі, і обходимо так
 * само: заглушуємо `Select` легким `<select>`, що приймає ті самі проп-и
 * (`data`/`value`/`onChange`/`label`/`aria-label`/`placeholder`) і
 * керується звичайним `fireEvent.change`, без порталу й без floating-ui.
 *
 * ⚠ Предмет ЦІЄЇ картки — колонка розв'язаної назви — не залежить від
 * СПРАВЖНЬОГО вигляду випадного списку: заглушник не бере участі в
 * перевірці, лише дає спосіб обрати роль, і не чіпає `NumberInput`/`Switch`/
 * `Button`, які під jsdom так не зависають.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  type StubOption = { value: string; label: string };
  type StubSelectProps = {
    data?: (string | StubOption)[];
    value?: string | null;
    onChange?: (value: string | null) => void;
    label?: string;
    placeholder?: string;
    'aria-label'?: string;
  };

  function StubSelect(props: StubSelectProps): JSX.Element {
    const options = (props.data ?? []).map((item) =>
      typeof item === 'string' ? { value: item, label: item } : item,
    );

    return (
      <select
        aria-label={props['aria-label'] ?? props.label ?? props.placeholder}
        value={props.value ?? ''}
        onChange={(event) => props.onChange?.(event.target.value === '' ? null : event.target.value)}
      >
        <option value="" />
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    );
  }

  return { ...actual, Select: StubSelect };
});

const SeededStrings: Record<string, string> = {
  'security.role': 'Role',
  'grants.pickRole': 'Pick a role',
  'grants.pickRoleHint': 'Pick a role hint',
  'grants.add': 'Add grant',
  'grants.saved': 'Saved',
  'grants.kind': 'Resource kind',
  'grants.resource': 'Resource id',
  'grants.resourceName': 'Resolved name',
  'grants.resourceNameUnknown': 'Not found — resource deleted or the id is wrong',
  'grants.level': 'Level',
  'grants.deny': 'Deny',
  'grants.remove': 'Remove',
  'grants.empty': 'Empty',
  'grants.emptyHint': 'Empty hint',
  'common.save': 'Save',
  // ⚠ `AsyncBoundary` показує це в стані очікування (`common.loading`) —
  // без нього рядок не падає (`t()` повертає видимий заповнювач, не кидає
  // винятку), але консоль скаржиться на кожному прогоні дарма.
  'common.loading': 'Loading',
};

const Roles: RoleView[] = [
  {
    id: 10,
    code: 'Auditor',
    isActive: true,
    isBuiltIn: false,
    permissions: [],
    dangerousPermissions: [],
  },
];

const GrantsResponse = [
  { resourceKind: 'Sheet', resourceId: 501, level: 'Read', isDeny: false, resourceName: 'BS' },
  { resourceKind: 'Table', resourceId: 999, level: 'Write', isDeny: false, resourceName: null },
];

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return new Response(
          JSON.stringify({ languageCode: 'en', revision: 1, strings: SeededStrings }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/grants')) {
        return new Response(JSON.stringify(GrantsResponse), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify([]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

beforeEach(() => {
  stubFetch();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderPanel() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <GrantsPanel roles={Roles} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * Обирає роль через заглушений `<select>` (див. `vi.mock('@mantine/core')`
 * вище) — звичайний `fireEvent.change`, без відкриття справжнього спливного
 * списку.
 */
function selectRole(name: string): void {
  const role = Roles.find((r) => r.code === name);
  if (!role) throw new Error(`Немає такої ролі в фікстурі: ${name}`);

  fireEvent.change(screen.getByLabelText('Role'), { target: { value: String(role.id) } });
}

describe('GrantsPanel: колонка розв\'язаної назви ресурсу (Q-299)', () => {
  it('показує розв\'язаний код для наявного ресурсу і явний стан для «осиротілого» посилання', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPanel();

    await screen.findByLabelText('Role');
    selectRole('Auditor');

    // Ресурс, який резолвер знайшов: код видно поряд із числовим id.
    // ⚠ `findByText` сам кидає виняток, якщо елемента немає — додаткове
    // `expect(...).not.toBeNull()` перевіряє те саме, без матчера
    // `jest-dom`, якого цей проєкт свідомо не підключає (`SecurityPage`-
    // тести поруч роблять так само).
    const resolved = await screen.findByText('BS');
    expect(resolved).not.toBeNull();

    // ⛔ Мутаційний доказ: `null` — це НЕ порожня комірка і не сам код, а
    // окремий видимий стан. Якби компонент показував `grant.resourceName`
    // напряму без розрізнення `null`, ця комірка була б порожньою.
    const unknown = await screen.findByText('Not found — resource deleted or the id is wrong');
    expect(unknown).not.toBeNull();

    // Обидва рядки таблиці справді мають по одній комірці кожного стану —
    // перевіряємо, що вони в тій самій таблиці, що й дані ролі.
    // ⚠ `getByDisplayValue`, не `getByText`: `resourceId` малює `NumberInput`
    // — число живе в атрибуті `value` вхідного поля, а не текстовим вузлом.
    const table = resolved.closest('table');
    expect(table).not.toBeNull();
    expect(within(table as HTMLElement).getByDisplayValue('501')).not.toBeNull();
    expect(within(table as HTMLElement).getByDisplayValue('999')).not.toBeNull();
  });

  it('щойно доданий (ще не збережений) грант показує «—», а не вигадану назву чи помилку', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPanel();

    await screen.findByLabelText('Role');
    selectRole('Auditor');

    await screen.findByText('BS');

    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));

    // ⛔ Новий рядок ще не пройшов через сервер: немає ні коду, ні
    // «Not found» — лише нейтральний прочерк, доки чернетку не збережено.
    const dashes = screen.getAllByText('—');
    expect(dashes.length).toBeGreaterThan(0);
  });
});
