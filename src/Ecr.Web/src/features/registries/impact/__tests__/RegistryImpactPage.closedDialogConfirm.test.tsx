import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';
import { RegistryImpactPage } from '../RegistryImpactPage';

/**
 * B145: діалог причини перерахунку, що вже закрився, не ставить ДРУГИЙ перерахунок — та ще й «усіх».
 *
 * ⛔ Діра (зонд на базі, без захисту в `shared/ui/ReasonModal`): обрано DOC7 → «Recalculate» → причина →
 * підтвердження: `POST {documentIds:[7]}`. `onConfirm` закриває діалог ОДРАЗУ (`setAsking(false)`), а після
 * відповіді сервера (`202`) зайнятість знято й вибір скинуто (`setSelected(new Set())`). Кнопка діалогу з
 * причиною в полі ще ~200 мс у DOM — другий клік подвійного кліку слав
 * `POST {documentIds:null, reason:<та сама>}`, тобто перерахунок УСІХ зачеплених документів (бюджет прогону,
 * `D-63`, `R-14`), якого людина не просила.
 *
 * Захист — корінь класу: `shared/ui/ReasonModal` не викликає `onConfirm`, коли `opened = false`. Власного
 * захисту сторінка не має і не потребує. Мутаційний доказ: повернути в `ReasonModal.tsx`
 * `onClick={() => onConfirm(reason.trim())}` → цей тест червоний
 * (`expected [ { documentIds: [ 7 ], …(1) }, …(1) ] to deeply equal [ { documentIds: [ 7 ], …(1) } ]`).
 *
 * ⚠ Без залежності від часу: `ReasonModal` справжній, тема `keepMountedModalsTheme` лишає закритий діалог у
 * DOM назавжди, відповідь сервера віддає тест.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const Doc5 = { documentId: 5, businessKey: 'DOC5', periodKey: 202609, periodState: 'Open', via: ['methodology:HSE301'] };
const Doc7 = { documentId: 7, businessKey: 'DOC7', periodKey: 202608, periodState: 'Grace', via: ['methodology:HSE301'] };

const posts: unknown[] = [];
let respond: (() => void) | null = null;

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Registry.View', 'Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      // ⚠ Поки задача рахується, сервер повертає той самий перелік: документи на місці, вибір — ні.
      if (path.endsWith('/impact')) return json({ items: [Doc5, Doc7], total: 2, truncated: false });

      if (path.endsWith('/recalculate-impacted') && init?.method === 'POST') {
        posts.push(JSON.parse(String(init.body)));
        // Сервер відповідає тоді, коли скаже тест.
        await new Promise<void>((resolve) => {
          respond = resolve;
        });

        return json({ jobId: 'IRegistryImpactRecalculationJob#1' }, 202);
      }

      if (path.includes('/api/v1/jobs/')) {
        return json({ jobId: 'IRegistryImpactRecalculationJob#1', state: 'Running', percent: 10, message: null, error: null });
      }

      return json(null);
    }),
  );
}

afterEach(() => {
  posts.length = 0;
  respond = null;
  vi.unstubAllGlobals();
});

describe('B145: перерахунок зачеплених — діалог причини, що закрився', () => {
  it('клік по кнопці діалогу після відповіді сервера не ставить другого перерахунку (усіх)', async () => {
    mockServer();
    render(
      <MantineProvider theme={keepMountedModalsTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter initialEntries={['/admin/registries/COMPONENT/impact']}>
            <Routes>
              <Route path="/admin/registries/:code/impact" element={<RegistryImpactPage />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByRole('checkbox', { name: /DOC7/ }));
    fireEvent.click(document.querySelector<HTMLButtonElement>('[data-impact-recalculate]') as HTMLButtonElement);

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'лише DOC7' } });
    const confirm = within(dialog).getByRole('button', { name: /registries\.impact\.recalculateConfirm/ });

    fireEvent.click(confirm);
    await waitFor(() => {
      expect(posts).toEqual([{ documentIds: [7], reason: 'лише DOC7' }]);
    });

    // Сервер прийняв: зайнятість знято, вибір скинуто (галочка знята) — діалог давно закрито (`asking = false`).
    respond?.();
    await waitFor(() => {
      expect((screen.getByRole('checkbox', { name: /DOC7/ }) as HTMLInputElement).checked).toBe(false);
      expect(confirm).toHaveProperty('disabled', false);
    });
    // Закритий діалог схований (умова лише настає й тримається — від часу анімації результат не залежить).
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM, не вимкнена, причина ще в полі.
    expect(confirm.isConnected).toBe(true);
    fireEvent.click(confirm);

    // Незахищена мутація стартує одразу: кілька тіків циклу подій — і другий POST уже був би в журналі.
    await new Promise<void>((resolve) => setImmediate(resolve));
    await new Promise<void>((resolve) => setImmediate(resolve));
    expect(posts).toEqual([{ documentIds: [7], reason: 'лише DOC7' }]);
  });
});
