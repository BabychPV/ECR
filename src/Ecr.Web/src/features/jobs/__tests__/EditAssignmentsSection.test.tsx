import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { EditAssignmentsSection } from '@/features/jobs/EditAssignmentsSection';
import { editAssignmentHref, pendingEditAssignmentCount } from '@/features/editAssignments/model';
import type { MyEditAssignmentsView } from '@/features/editAssignments/types';

/**
 * Секції передач у «My tasks» (EA-5c): мок `GET /api/v1/me/edit-assignments` на контракті з DESIGN-OWNER-EDIT-GRANTS.
 * Мутація: приберіть показ `returnReason` або `?panel=edit-assignments` для валідації - відповідні тести червоні.
 */

const calls: string[] = [];

const payload: MyEditAssignmentsView = {
  assignedToMe: [
    { assignmentId: 1, documentId: 11, documentKey: 'AIR-2026-01', slice: 'January', status: 'Assigned', note: 'due 5th' },
    {
      assignmentId: 2,
      documentId: 12,
      documentKey: 'AIR-2026-02',
      slice: 'February',
      status: 'Returned',
      returnReason: '<b>Fix</b> units',
    },
  ],
  awaitingMyValidation: [
    { assignmentId: 3, documentId: 13, documentKey: 'AIR-2026-03', slice: 'March', status: 'Filled', filledAt: '2026-10-07T09:30:00Z' },
  ],
};

function show(body: unknown, status = 200): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.pathname + input.search : input.url;
      calls.push(url);

      return Promise.resolve(
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <EditAssignmentsSection opened />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  calls.length = 0;
  vi.unstubAllGlobals();
});

describe('EditAssignmentsSection', () => {
  it('«Assigned to me»: рядки з коментарем повернення (екранованим) і переходом у документ', async () => {
    show(payload);

    const section = await screen.findByTestId('my-tasks-assigned');
    expect(section.textContent).toContain('AIR-2026-01');
    expect(section.textContent).toContain('January');
    expect(section.textContent).toContain('due 5th');

    const reason = screen.getByTestId('edit-assignment-return-reason');
    expect(reason.textContent).toContain('<b>Fix</b> units');
    expect(reason.querySelector('b')).toBeNull();

    const links = section.querySelectorAll('a[data-action="open"]');
    expect(Array.from(links).map((a) => a.getAttribute('href'))).toEqual(['/documents/11', '/documents/12']);
    expect(section.querySelector('a[data-action="validate"]')).toBeNull();
    expect(calls).toEqual(['/api/v1/me/edit-assignments']);
  });

  it('«Awaiting my validation»: Open і Validate… ведуть у шторку передач документа', async () => {
    show(payload);

    const section = await screen.findByTestId('my-tasks-validation');
    expect(section.textContent).toContain('AIR-2026-03');
    expect(section.querySelector('a[data-action="open"]')?.getAttribute('href')).toBe('/documents/13');
    expect(section.querySelector('a[data-action="validate"]')?.getAttribute('href')).toBe(
      '/documents/13?panel=edit-assignments',
    );
  });

  it('порожні відповіді мовчать, без секцій', async () => {
    show({ assignedToMe: [], awaitingMyValidation: [] });

    await waitFor(() => expect(calls).toHaveLength(1));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.queryByTestId('my-tasks-assigned')).toBeNull();
    expect(screen.queryByTestId('my-tasks-validation')).toBeNull();
    expect(screen.queryByTestId('my-tasks-edit-error')).toBeNull();
  });

  it.each([404, 403])('%i - тихий порожній стан: без помилки й без секцій (передач на стенді ще немає)', async (status) => {
    show({ code: 'ECR-GEN-0404' }, status);

    await waitFor(() => expect(calls).toHaveLength(1));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.queryByTestId('my-tasks-edit-error')).toBeNull();
    expect(screen.queryByTestId('my-tasks-assigned')).toBeNull();
    expect(screen.queryByTestId('my-tasks-validation')).toBeNull();
  });

  it('відмова видима, з повтором', async () => {
    show({ code: 'ECR-SYS-0500' }, 500);

    const error = await screen.findByTestId('my-tasks-edit-error');
    const retry = error.querySelector('button');
    expect(retry).not.toBeNull();
    fireEvent.click(retry as HTMLElement);
    await waitFor(() => expect(calls.length).toBeGreaterThanOrEqual(2));
  });
});

describe('editAssignments helpers', () => {
  it('рахує обидві секції; undefined - нуль', () => {
    expect(pendingEditAssignmentCount(payload)).toBe(3);
    expect(pendingEditAssignmentCount(undefined)).toBe(0);
  });

  it('href: валідація відкриває шторку, відкриття - лише документ', () => {
    expect(editAssignmentHref(5, true)).toBe('/documents/5?panel=edit-assignments');
    expect(editAssignmentHref(5, false)).toBe('/documents/5');
  });
});
