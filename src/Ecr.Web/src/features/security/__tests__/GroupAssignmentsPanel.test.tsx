import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent, { PointerEventsCheckLevel } from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { GroupAssignmentsPanel, toMachineDate } from '@/features/security/GroupAssignmentsPanel';
import { withTestDefaults } from '@/test/render';
import type { RoleView } from '@/api/types';

/** Ролі груп каталогу: ім'я поруч із SID, відкликання, підтвердження небезпечної ролі. */

const Strings: Record<string, string> = {
  'security.role': 'Role',
  'groupRoles.title': 'Group roles',
  'groupRoles.group': 'Group',
  'groupRoles.revoke': 'Revoke',
  'groupRoles.revoked': 'Revoked',
  'groupRoles.principal': 'Group name or SID',
  'groupRoles.principalHint': 'DOMAIN\\Group or S-1-...',
  'groupRoles.assign': 'Assign role to group',
  'groupRoles.assigned': 'Assigned',
  'groupRoles.assignedNextSignIn': 'Assigned, next sign-in',
  'groupRoles.dangerousTitle': 'Dangerous permissions',
  'groupRoles.assignAnyway': 'Assign anyway',
  'groupRoles.validFrom': 'Valid from',
  'groupRoles.validTo': 'Valid to',
  'groupRoles.validityOrder': 'End before start',
};

const roles: RoleView[] = [
  { id: 2, code: 'Publishers', isActive: true, isBuiltIn: false, permissions: [], dangerousPermissions: ['Calculation.Publish'] },
];

const json = (body: unknown, status = 200, type = 'application/json'): Response =>
  new Response(body === null ? null : JSON.stringify(body), { status, headers: { 'Content-Type': type } });

function stubFetch(
  onPost: (body: Record<string, unknown>) => Response,
  validity: { validFrom: string | null; validTo: string | null } = { validFrom: null, validTo: null },
): ReturnType<typeof vi.fn> {
  const spy = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
    if (init?.method === 'POST') return onPost(JSON.parse(String(init.body)) as Record<string, unknown>);
    if (init?.method === 'DELETE') return json(null, 204);
    return json([
      { id: 11, roleId: 2, roleCode: 'Publishers', principalSid: 'S-1-5-21-1-2-3-1105', principalName: 'CORP\\EcrOps', ...validity },
    ]);
  });
  vi.stubGlobal('fetch', spy);
  return spy;
}

async function renderPanel(): Promise<void> {
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <GroupAssignmentsPanel roles={roles} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/*
 * ⛔ Чому тут не `userEvent.*` напряму і чому поля беруться за підписом, а не
 * за роллю. Файл падав таймаутом (5000 мс) у повному наборі під навантаженням,
 * проходячи поодинці. Профіль випадку з датами (`node:inspector`): ~40 % CPU —
 * `window.getComputedStyle` jsdom (377 з 907 мс), і приходить він із ДВОХ
 * місць: `getByRole`/`findByRole` (`queryAllByRole` — 359 мс: роль кожного
 * вузла, перевірка «чи прихований» і доступне ім'я йдуть по предках до
 * `<html>`) та перевірки `pointer-events` у `user-event` перед кожною дією
 * вказівника; ще `userEvent.tab()` будує список фокусованих із тією ж
 * перевіркою видимості. У jsdom `getComputedStyle` не дешевий: кожен виклик
 * проганяє вбудовану таблицю стилів браузера через `nwsapi` для елемента й
 * предків, а кеш скидається БУДЬ-ЯКОЮ мутацією DOM, тобто після кожного
 * рендера. ⚠ Змінні теми Mantine тут ні до чого: `withCssVariables={false}`
 * виміряно — частка не змінилася.
 *
 * Після правки той самий профіль: `getComputedStyle` 56–77 мс, `queryAllByRole`
 * 12–15 мс; лишився рендер самого React (~320 мс), тобто ціна компонента, а
 * не запитів тесту. Випадок поодинці: 2.4–3.0 с → 0.65–1.2 с.
 *
 *  • `pointerEventsCheck: Never` — перевірка тут порожня: CSS Mantine у
 *    тестах не завантажується (`css` у vitest вимкнено), тож `pointer-events:
 *    none` взятися нізвідки, а ціна — прохід по предках на кожну дію.
 *  • `delay: null` — без `setTimeout` між діями: під навантаженням кожен
 *    таймер запізнюється, а предмет цих випадків — не темп введення.
 *  • Поля — `getByLabelText`: зв'язок «підпис → поле» перевіряється так само,
 *    але без обходу всього дерева з обчисленням стилів.
 *  • Без `userEvent.tab()` між датами: `DateInput` кладе розібрану дату в стан
 *    уже на `change`, а не на втраті фокуса, — «End before start» з'являється
 *    й без нього (випадок це й доводить: повідомлення чекається `findByText`).
 *  • Кнопку «Assign role to group» знайдено ОДИН раз: вузол той самий на всіх
 *    рендерах, а повторний `getByRole` після розкритих випадних блоків —
 *    найдорожчий запит випадку.
 */
const user = (): ReturnType<typeof userEvent.setup> =>
  userEvent.setup({ delay: null, pointerEventsCheck: PointerEventsCheckLevel.Never });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('GroupAssignmentsPanel', () => {
  it('показує імʼя поруч із SID і відкликає призначення за його Id', async () => {
    const fetchSpy = stubFetch(() => json({}, 201));
    await renderPanel();

    expect(await screen.findByText('CORP\\EcrOps')).not.toBeNull();
    expect(screen.getByText('S-1-5-21-1-2-3-1105')).not.toBeNull();

    await user().click(screen.getByRole('button', { name: 'Revoke' }));

    await waitFor(() => {
      const call = fetchSpy.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'DELETE');
      expect(String(call?.[0])).toContain('/api/v1/security/group-assignments/11');
    });
  });

  it('небезпечна роль: 409 показує права, і лише «Assign anyway» шле confirmDangerous', async () => {
    const posted: Record<string, unknown>[] = [];
    stubFetch((body) => {
      posted.push(body);
      return body['confirmDangerous'] === true
        ? json({ id: 12, principalSid: 'S-1-5-32-544', principalName: null, effectiveAfterNextSignIn: true }, 201)
        : json(
            {
              title: 'Conflict',
              status: 409,
              detail: 'Dangerous',
              errorCode: 'ECR-SEC-0409',
              correlationId: 'c-1',
              dangerousPermissions: ['Calculation.Publish'],
            },
            409,
            'application/problem+json',
          );
    });
    await renderPanel();
    const u = user();
    const assign = screen.getByRole('button', { name: 'Assign role to group' });

    await u.click(screen.getByLabelText('Role'));
    await u.click(await screen.findByRole('option', { name: 'Publishers' }));
    // Предмет випадку — підтвердження небезпечної ролі, а не посимвольний ввід.
    fireEvent.change(screen.getByLabelText(/Group name or SID/), { target: { value: 'S-1-5-32-544' } });
    await u.click(assign);

    expect(await screen.findByText('Calculation.Publish')).not.toBeNull();
    expect(posted).toEqual([{ roleId: 2, principal: 'S-1-5-32-544', confirmDangerous: false }]);

    await u.click(screen.getByRole('button', { name: 'Assign anyway' }));

    await waitFor(() => expect(posted.at(-1)).toEqual({ roleId: 2, principal: 'S-1-5-32-544', confirmDangerous: true }));
  });

  it('вікно чинності: дата без години, а безстрокова межа — «…», не «—»', async () => {
    stubFetch(() => json({}, 201), { validFrom: '2026-03-01', validTo: null });
    await renderPanel();

    const row = (await screen.findByText('CORP\\EcrOps')).closest('tr');
    const from = row?.querySelector('time');

    expect(from?.getAttribute('datetime')).toBe('2026-03-01');
    expect(from?.textContent).toContain('2026');
    // Години в даних немає: `DateOnly` на сервері, тож і на екрані її бути не може.
    expect(from?.textContent).not.toMatch(/\d:\d\d/);

    const open = row?.querySelector('[data-timestamp="none"]');
    expect(open?.textContent).toBe('…');
    expect(row?.textContent).not.toContain('—');
  });

  describe('дата в запиті — та сама доба, яку обрано', () => {
    /*
     * ⚠ Пояс у тесті задати не можна: пул `vmThreads` — це потоки, а присвоєння
     * `process.env.TZ` у потоці до рушія не доходить (перевірено: мутація
     * `toISOString()` лишала «New_York 23:30» зеленим). Тому два виміри:
     *   • північ і пізній вечір ЛОКАЛЬНОЇ доби — у будь-якому поясі з ненульовим
     *     зсувом `toISOString()` ламає рівно один із них;
     *   • дата, чия локальна доба свідомо не збігається з UTC, — ловить те саме
     *     і на машині в UTC (CI).
     */
    it.each([0, 23])('toMachineDate бере локальну добу, година %i', (hour) => {
      expect(toMachineDate(new Date(2026, 2, 1, hour, 30))).toBe('2026-03-01');
      expect(toMachineDate(null)).toBeNull();
    });

    it('toMachineDate не читає UTC: локальна доба 2 березня за UTC-доби 1 березня', () => {
      const picked = new Date(Date.UTC(2026, 2, 1, 12));
      picked.getFullYear = () => 2026;
      picked.getMonth = () => 2;
      picked.getDate = () => 2;

      expect(toMachineDate(picked)).toBe('2026-03-02');
    });

    it('форма шле validFrom/validTo як YYYY-MM-DD, а «to раніше from» не відправляється', async () => {
      const posted: Record<string, unknown>[] = [];
      stubFetch((body) => {
        posted.push(body);
        return json({ id: 13, principalSid: 'S-1-5-32-544', principalName: null, effectiveAfterNextSignIn: false }, 201);
      });
      await renderPanel();
      const u = user();
      const assign = screen.getByRole('button', { name: 'Assign role to group' });
      const validTo = screen.getByLabelText('Valid to');

      await u.click(screen.getByLabelText('Role'));
      await u.click(await screen.findByRole('option', { name: 'Publishers' }));
      // ⚠ Дати вводяться `fireEvent.change`, а не посимвольним `userEvent.type`:
      // предмет випадку — ФОРМАТ відправленого тіла, а не ввід. П'ять полів по
      // десять символів з'їдали майже всю стелю vitest (5000 мс), і випадок падав
      // таймаутом у повному наборі під навантаженням, проходячи поодинці.
      fireEvent.change(screen.getByLabelText(/Group name or SID/), { target: { value: 'S-1-5-32-544' } });
      fireEvent.change(screen.getByLabelText('Valid from'), { target: { value: '2026-03-10' } });
      fireEvent.change(validTo, { target: { value: '2026-03-01' } });

      expect(await screen.findByText('End before start')).not.toBeNull();
      expect(assign).toHaveProperty('disabled', true);

      fireEvent.change(validTo, { target: { value: '2026-03-31' } });
      await u.click(assign);

      await waitFor(() =>
        expect(posted).toEqual([
          { roleId: 2, principal: 'S-1-5-32-544', confirmDangerous: false, validFrom: '2026-03-10', validTo: '2026-03-31' },
        ]),
      );
    });
  });

  it('перелік не приїхав — причина з кодом, а НЕ порожня таблиця «нічого не призначено»', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) =>
      String(input).includes('/ui-strings/')
        ? json({ languageCode: 'en', revision: 1, strings: Strings })
        : json({ type: 'about:blank', title: 'Error', status: 500, errorCode: 'ECR-SYS-0500', correlationId: 'c' }, 500, 'application/problem+json'),
    ));
    await renderPanel();

    expect((await screen.findByRole('alert')).textContent ?? '').toContain('ECR-SYS-0500');
    // ⛔ Після банера: заголовки порожньої таблиці — і є «нормальний порожній стан».
    expect(screen.queryByRole('table')).toBeNull();
    // Форма призначення від переліку не залежить і лишається.
    expect(screen.getByRole('button', { name: 'Assign role to group' })).not.toBeNull();
  });
});
