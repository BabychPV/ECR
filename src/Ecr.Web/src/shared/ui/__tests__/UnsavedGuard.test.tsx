import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { createMemoryRouter, Link, Outlet, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { UnsavedGuard } from '@/shared/ui/UnsavedGuard';
import { cancelAutosave, registerSliceSaver } from '@/features/grid/autosave';
import {
  discardPendingRows,
  openDocument,
  pendingCount,
  putPendingEdit,
  resetPending,
} from '@/features/grid/pendingStore';
import type { PendingEdit } from '@/features/grid/useCellPatch';

/**
 * `D14-12` крок 3: вихід із документа з незбереженими правками.
 *
 * Три твердження, кожне окремим тестом, і кожне — про РІЗНУ гілку рішення:
 *   1. правки є, збереження ВДАЛОСЬ → перехід мовчки, без діалогу;
 *   2. правки є, збереження ВПАЛО → діалог; «Залишитись» скасовує перехід;
 *   3. правок НЕМА → `useBlocker` не втручається взагалі.
 *
 * ⛔ Третє твердження перевіряється не «сторінка відкрилася» — вона відкрилася б
 * і при безумовному блокуванні, бо порожнє сховище відстоюється миттєво і
 * блокувальник сам себе пропустив би. Перевіряється ФАКТ невтручання:
 * збереження (`flushUnsaved` реєстру) не було викликано жодного разу. Саме
 * тому реєстр тут обгорнутий лічильником, а не підмінений заглушкою —
 * поведінка лишається справжньою, додається лише свідок.
 *
 * ⚠ Сітку сторож тепер бачить лише через реєстр (`unsavedSources.ts`), а
 * джерело `grid` реєструється імпортом `@/features/grid/autosave` нижче. Свідок
 * стоїть на реєстрі, бо саме його викликає сторож.
 */

const settleCalls = vi.hoisted(() => ({ count: 0 }));

vi.mock('@/shared/ui/unsavedSources', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/shared/ui/unsavedSources')>();

  return {
    ...actual,
    flushUnsaved: async (timeoutMs?: number): Promise<boolean> => {
      settleCalls.count += 1;

      return await actual.flushUnsaved(timeoutMs);
    },
  };
});

const Document = 7;
const Table = 700;
const Period = 202609;

/** Таймаут відстоювання в тестах: три секунди продукту — це три секунди набору. */
const SettleMs = 50;

function edit(rowKey: string, columnCode: string, value: unknown): PendingEdit {
  return { rowKey, columnCode, value, isEmpty: false, baseVersion: 'v1' };
}

const unregister: (() => void)[] = [];

/** Зберігач, який ВДАЛО зберіг: підтверджені рядки зникають зі сховища. */
function savingSaverSucceeds(): void {
  unregister.push(
    registerSliceSaver(Table, Period, (edits) => {
      discardPendingRows(
        Table,
        Period,
        edits.map((item) => item.rowKey),
      );
    }),
  );
}

/**
 * Зберігач, у якого збереження ВПАЛО.
 *
 * ⚠ Нічого не робить саме тому, що так виглядає відмова в цій системі: сітка
 * показує банер, а правки ЛИШАЮТЬСЯ у сховищі (`DocumentGrid.save()`, гілка
 * `catch` — `discardPendingRows` там не викликається).
 */
function savingSaverFails(): void {
  unregister.push(
    registerSliceSaver(Table, Period, () => {
      // Сервер відмовив: жоден рядок не підтверджено.
    }),
  );
}

function renderAtDocument(): void {
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: (
          <>
            <UnsavedGuard settleTimeoutMs={SettleMs} />
            <Outlet />
          </>
        ),
        children: [
          { path: 'documents/7', element: <Link to="/documents">до переліку</Link> },
          {
            path: 'documents',
            element: <p data-testid="documents-list">перелік документів</p>,
          },
        ],
      },
    ],
    { initialEntries: ['/documents/7'] },
  );

  render(
    <MantineProvider theme={theme}>
      <RouterProvider router={router} />
    </MantineProvider>,
  );
}

beforeEach(() => {
  settleCalls.count = 0;
});

afterEach(() => {
  cleanup();
  for (const off of unregister.splice(0)) off();
  cancelAutosave();
  resetPending();
});

describe('D14-12 · вихід із документа з незбереженими правками', () => {
  it('збереження вдалося — перехід проходить МОВЧКИ, без жодного діалогу', async () => {
    const user = userEvent.setup();

    openDocument(Document);
    putPendingEdit(Table, Period, edit('R1', 'C1', 12.4));
    savingSaverSucceeds();

    renderAtDocument();

    await user.click(screen.getByText('до переліку'));

    // ⛔ Ось вимога директиви дослівно: «діалог лише якщо збереження не
    // вдалося». Питання на кожному переході навчає відповідати не читаючи.
    expect(await screen.findByTestId('documents-list')).toBeDefined();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(pendingCount()).toBe(0);

    // Свідок: блокувальник таки втрутився і таки зберіг — просто мовчки.
    expect(settleCalls.count).toBe(1);
  });

  it('збереження впало — діалог з’являється, «Залишитись» скасовує перехід, правки на місці', async () => {
    const user = userEvent.setup();

    openDocument(Document);
    putPendingEdit(Table, Period, edit('R1', 'C1', 12.4));
    savingSaverFails();

    renderAtDocument();

    await user.click(screen.getByText('до переліку'));

    const dialog = await screen.findByRole('dialog');
    expect(dialog).toBeDefined();
    expect(screen.queryByTestId('documents-list')).toBeNull();

    await user.click(screen.getByTestId('unsaved-stay'));

    // Лишились там, де дані: сторінка документа, правка в сховищі.
    expect(screen.getByText('до переліку')).toBeDefined();
    expect(screen.queryByTestId('documents-list')).toBeNull();
    expect(pendingCount()).toBe(1);
  });

  it('незбережених правок немає — блокувальник не втручається взагалі', async () => {
    const user = userEvent.setup();

    openDocument(Document);

    renderAtDocument();

    await user.click(screen.getByText('до переліку'));

    expect(await screen.findByTestId('documents-list')).toBeDefined();
    expect(screen.queryByRole('dialog')).toBeNull();

    // ⛔ Саме це й ламає безумовне блокування (`shouldBlock: () => true`):
    // перехід усе одно відбувся б, але через збереження, якого не треба було.
    expect(settleCalls.count).toBe(0);
  });
});
