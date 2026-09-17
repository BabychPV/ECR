import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, within, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { GrantsPanel } from '@/pages/admin/GrantsPanel';
import type { RoleView } from '@/api/types';
import { withTestDefaults } from '@/test/render';

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
 * ✎ Тут стояв «~166с на ОДИН клік по `Select`» і заглушка всього
 * `@mantine/core`. Вимір був правдивий, а пояснення — ні: справа не в
 * Mantine чи floating-ui, а у взаємній рекурсії jsdom ↔ nwsapi на станових
 * псевдокласах (`:modal`/`:fullscreen`), яку запускає пастка фокуса
 * випадного списку. Обрив рекурсії живе в `src/test/setup.ts`; той самий
 * клік тепер коштує мілісекунди, тож заглушку прибрано і роль обирається у
 * справжньому `Select`.
 */

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
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={client}>
        <GrantsPanel roles={Roles} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * Обирає роль у справжньому `Select`: клік по полю розкриває список, клік по
 * опції обирає її.
 *
 * ⚠ Опції рендеряться в порталі поза деревом панелі — звідси `screen`.
 */
async function selectRole(name: string): Promise<void> {
  if (!Roles.some((r) => r.code === name)) {
    throw new Error(`Немає такої ролі в фікстурі: ${name}`);
  }

  fireEvent.click(screen.getByLabelText('Role'));
  fireEvent.click(await screen.findByRole('option', { name }));
}

describe('GrantsPanel: колонка розв\'язаної назви ресурсу (Q-299)', () => {
  it('показує розв\'язаний код для наявного ресурсу і явний стан для «осиротілого» посилання', async () => {
    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    renderPanel();

    await screen.findByLabelText('Role');
    await selectRole('Auditor');

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
    await selectRole('Auditor');

    await screen.findByText('BS');

    fireEvent.click(screen.getByRole('button', { name: 'Add grant' }));

    // ⛔ Новий рядок ще не пройшов через сервер: немає ні коду, ні
    // «Not found» — лише нейтральний прочерк, доки чернетку не збережено.
    const dashes = screen.getAllByText('—');
    expect(dashes.length).toBeGreaterThan(0);
  });
});
