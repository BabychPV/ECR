import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { mantineProviderProps } from '@/shared/theme/provider';
import { EditAssignmentsDrawer } from '@/features/editAssignments/EditAssignmentsDrawer';
import { ExecutorStrip } from '@/features/editAssignments/ExecutorStrip';
import { EditAssignmentsSection } from '@/features/jobs/EditAssignmentsSection';
import { Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { DocumentId, mockServer, myAssignment, type Sent } from './fixture';

/**
 * Доступність потоків «Передача місяця» (`ФВ-14.16`): діалоги «Валідувати» / «Повернути» / «Видалити»,
 * смуга виконавця з підтвердженням «Заповнено», секції «Мої задачі» - в обох темах, плюс клавіатура
 * (меню рядка -> діалог -> Esc -> фокус на кнопці рядка).
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

function wrap(colorScheme: 'light' | 'dark', children: React.ReactNode, route = '/?panel=edit-assignments'): React.JSX.Element {
  return (
    <MantineProvider {...mantineProviderProps} forceColorScheme={colorScheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

async function openRowMenu(user: ReturnType<typeof userEvent.setup>, rowIndex: number): Promise<HTMLElement> {
  const triggers = await screen.findAllByRole('button', { name: /editAssign\.rowActions/ });
  const trigger = triggers[rowIndex] as HTMLElement;
  await user.click(trigger);

  return trigger;
}

describe('Передача місяця: діалоги шторки - доступність і клавіатура', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: «Валідувати», «Повернути», «Видалити» - без блокуючих порушень`, async () => {
      mockServer([] as Sent[]);
      const user = userEvent.setup();
      render(wrap(colorScheme, <EditAssignmentsDrawer documentId={DocumentId} sheets={[]} />));

      await screen.findByTestId('edit-assignments-months');

      // Рядок 0 - січень (Filled): доступні всі три дії.
      for (const action of ['editAssign.validate', 'editAssign.return', 'editAssign.remove']) {
        const trigger = await openRowMenu(user, 0);
        await user.click(await screen.findByRole('menuitem', { name: new RegExp(action.replace('.', '\\.')) }));

        const dialog = await screen.findByRole('dialog', { name: /editAssign\.(validateTitle|returnTitle|removeTitle)/ });
        const found = await findViolations(dialog);
        expect(found, describeViolations(found)).toHaveLength(0);

        await user.keyboard('{Escape}');
        await waitFor(() => expect(screen.queryByRole('dialog', { name: /editAssign\.(validateTitle|returnTitle|removeTitle)/ })).toBeNull());
        // Esc закриває лише діалог: шторка під ним лишається, а фокус повертається на кнопку рядка, не в body.
        expect(screen.getByTestId('edit-assignments-months')).toBeTruthy();
        await waitFor(() => expect(document.activeElement, `${action} фокус: ${document.activeElement?.outerHTML.slice(0, 120) ?? ""}`).toBe(trigger));
      }
    });
  }
});

describe('Передача місяця: майстер - клавіатура', { timeout: 120_000 }, () => {
  it('Esc у майстрі (відкритому з меню рядка) закриває лише майстер; фокус - на кнопку рядка', async () => {
    mockServer([] as Sent[]);
    const user = userEvent.setup();
    render(wrap('light', <EditAssignmentsDrawer documentId={DocumentId} sheets={[]} />));

    await screen.findByTestId('edit-assignments-months');
    const trigger = await openRowMenu(user, 0);
    await user.click(await screen.findByRole('menuitem', { name: /editAssign\.edit/ }));
    await screen.findByTestId('wizard-step-body');

    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByTestId('wizard-step-body')).toBeNull());
    expect(screen.getByTestId('edit-assignments-months')).toBeTruthy();
    await waitFor(() => expect(document.activeElement).toBe(trigger));
  });

  it('Esc у шторці без діалогу закриває шторку', async () => {
    mockServer([] as Sent[]);
    const user = userEvent.setup();
    render(wrap('light', <EditAssignmentsDrawer documentId={DocumentId} sheets={[]} />));

    await screen.findByTestId('edit-assignments-months');
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByTestId('edit-assignments-months')).toBeNull());
  });
});

describe('Передача місяця: смуга виконавця - доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: смуга і підтвердження «Заповнено» - без блокуючих порушень`, async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () =>
          new Response(
            JSON.stringify({
              assignedToMe: [
                myAssignment({ assignmentId: 11, documentId: DocumentId, month: 1 }),
                myAssignment({ assignmentId: 12, documentId: DocumentId, month: 2, status: 'Returned', returnReason: 'Check row 4' }),
              ],
              awaitingMyValidation: [],
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        ),
      );
      const user = userEvent.setup();
      render(wrap(colorScheme, <ExecutorStrip documentId={DocumentId} periodKey={202601} />, '/'));

      const strip = await screen.findByTestId('executor-strip');
      const bar = await findViolations(strip);
      expect(bar, describeViolations(bar)).toHaveLength(0);

      await user.click(within(strip).getAllByTestId('executor-strip-fill')[0] as HTMLElement);
      const dialog = await screen.findByRole('dialog');
      const confirm = await findViolations(dialog);
      expect(confirm, describeViolations(confirm)).toHaveLength(0);
    });
  }
});

describe('Передача місяця: секції «Мої задачі» - доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: «Assigned to me» і «Awaiting my validation» - без блокуючих порушень, посилання розрізнені`, async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () =>
          new Response(
            JSON.stringify({
              assignedToMe: [
                myAssignment({ assignmentId: 11, documentId: 1, month: 1 }),
                myAssignment({ assignmentId: 12, documentId: 2, month: 2, status: 'Returned', returnReason: 'Check row 4' }),
              ],
              awaitingMyValidation: [myAssignment({ assignmentId: 13, documentId: 3, month: 3, status: 'Filled' })],
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        ),
      );
      render(wrap(colorScheme, <EditAssignmentsSection opened />, '/'));

      const assigned = await screen.findByTestId('my-tasks-assigned');
      const found = await findViolations(assigned.parentElement as HTMLElement);
      expect(found, describeViolations(found)).toHaveLength(0);

      // WCAG 2.4.4: однакові «Open» у різних картках мають різну доступну назву.
      const names = screen.getAllByRole('link').map((link) => link.getAttribute('aria-label') ?? link.textContent ?? '');
      expect(new Set(names).size, `дублі доступних назв посилань: ${names.join(' | ')}`).toBe(names.length);
    });
  }
});
