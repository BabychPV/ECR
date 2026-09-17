import { StrictMode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

/**
 * Живий дефект, знайдений першоособовим UX-проходом (створення документа,
 * `CreateDocumentModal.tsx`, той самий клас), тепер підтверджений і тут:
 * чекбокс права в модалці створення ролі читав `event.currentTarget.checked`
 * ЛІНИВО, ВСЕРЕДИНІ функції-апдейтера `setRolePermissions`.
 *
 * ⛔ `event.currentTarget` — поле СИНТЕТИЧНОЇ події, і React обнуляє його
 * одразу після завершення обробника. `main.tsx` рендерить увесь застосунок
 * усередині `<StrictMode>`, а той навмисно кличе функцію-апдейтер `setState`
 * ДВІЧІ (щоб зловити нечисті апдейтери) — і на другому виклику
 * `event.currentTarget` уже `null`: `TypeError: Cannot read properties of
 * null (reading 'checked')`, і без `ErrorBoundary` на маршруті — увесь
 * застосунок замінюється голим «Unexpected Application Error!» React Router.
 *
 * ⚠ Чекбокс права — ПРОСТИЙ Mantine `Checkbox`, не `Select`/`Combobox`: на
 * відміну від `CreateDocumentModal.tsx` (де чекбокс аркуша ховається за
 * двома `Select`), сюди можна дійти справжньою взаємодією без ризику
 * зависання, задокументованого біля `SecurityPage.passwordToggle.test.tsx`
 * (те зависання — специфічно про клік по ОПЦІЇ випадного списку `Select`,
 * якого тут немає: кнопка «New role» відкриває модалку прямим кліком).
 */
const SeededStrings: Record<string, string> = {
  'security.roles': 'Roles',
  'security.grants': 'Grants',
  'security.users': 'Users',
  'security.createRole': 'New role',
  'security.roleCode': 'Code',
  'security.roleName': 'Name',
  'security.permissions': 'Permissions',
};

const Permissions = [{ code: 'Template.Edit', group: 'Template', isDangerous: false }];

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
      if (url.includes('/me')) {
        return new Response(
          JSON.stringify({
            userId: 0,
            userName: 'test',
            language: 'en',
            permissions: ['Security.ManageRoles'],
            isSimulation: false,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      if (url.includes('/permissions')) {
        return new Response(JSON.stringify(Permissions), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      // `/roles`, `/languages` — голі масиви, НЕ пагінована форма: ця
      // картка їх не зачіпає, порожній список цілком годящий.
      // `/languages` читає `LocalizedInput` (поле «Name» модалки).
      if (url.includes('/roles') || url.includes('/languages')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      // `/users` — пагінована відповідь (`UserPage`); ця картка її теж не
      // зачіпає.
      return new Response(JSON.stringify({ items: [], nextCursor: null, totalCount: 0 }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

function show() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <StrictMode>
      <MantineProvider theme={withTestDefaults(theme)}>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/security?tab=roles']}>
            <SecurityPage />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>
    </StrictMode>,
  );
}

describe('SecurityPage: чекбокс права в модалці створення ролі під StrictMode', () => {
  it('позначення права не розбиває застосунок', async () => {
    stubFetch();

    // ⚠ Тест монтує `SecurityPage` напряму, БЕЗ `AppLayout` — саме
    // `AppLayout` кличе `loadCatalog(me.language, 'private')` у своєму
    // ефекті (`SecurityPage.usersHeader.test.tsx` — той самий прийом).
    // Без цього виклику `loaded` лишається порожнім і кожен `t()` віддає
    // позначений ключ (`⟦security.createRole⟧`), незалежно від фікса, що
    // перевіряється тут.
    await loadCatalog('en', 'private');

    const user = userEvent.setup();
    show();

    const createButton = await screen.findByRole('button', { name: 'New role' });
    await user.click(createButton);

    const checkbox = await screen.findByRole('checkbox', { name: /Template\.Edit/ });

    // ⛔ ЧЕРВОНИЙ до фіксу: другий (StrictMode) виклик апдейтера
    // `setRolePermissions` кидав `TypeError`, і клік не завершився б штатно.
    await user.click(checkbox);

    expect((checkbox as HTMLInputElement).checked).toBe(true);
  });
});
