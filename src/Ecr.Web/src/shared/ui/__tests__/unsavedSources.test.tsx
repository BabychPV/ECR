import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { createMemoryRouter, Link, Outlet, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { UnsavedGuard } from '@/shared/ui/UnsavedGuard';
import {
  flushUnsaved,
  hasUnsavedChanges,
  registerUnsavedSource,
  unregisterUnsavedSource,
  unsavedCount,
} from '@/shared/ui/unsavedSources';

/**
 * Реєстр джерел незбережених змін — без сітки: сторож бачить лише реєстр.
 */

const offs: (() => void)[] = [];

afterEach(() => {
  cleanup();
  for (const off of offs.splice(0)) off();
  unregisterUnsavedSource('test');
});

function renderAtDocument(): void {
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: (
          <>
            <UnsavedGuard settleTimeoutMs={20} />
            <Outlet />
          </>
        ),
        children: [
          { path: 'doc', element: <Link to="/list">до переліку</Link> },
          { path: 'list', element: <p data-testid="list">перелік</p> },
        ],
      },
    ],
    { initialEntries: ['/doc'] },
  );

  render(
    <MantineProvider theme={theme}>
      <RouterProvider router={router} />
    </MantineProvider>,
  );
}

/** Джерело, що завжди має незбережене і зберегти не може. */
const stuck = {
  hasUnsaved: (): boolean => true,
  unsavedCount: (): number => 2,
  flush: async (): Promise<boolean> => await Promise.resolve(false),
};

describe('unsavedSources · реєстр для UnsavedGuard', () => {
  it('зареєстроване джерело з незбереженим блокує перехід (діалог)', async () => {
    registerUnsavedSource('test', stuck);
    const user = userEvent.setup();
    renderAtDocument();

    await user.click(screen.getByText('до переліку'));

    expect(await screen.findByRole('dialog')).toBeDefined();
    expect(screen.queryByTestId('list')).toBeNull();
  });

  it('після unregister той самий перехід проходить без блокування', async () => {
    registerUnsavedSource('test', stuck);
    unregisterUnsavedSource('test');
    const user = userEvent.setup();
    renderAtDocument();

    await user.click(screen.getByText('до переліку'));

    expect(await screen.findByTestId('list')).toBeDefined();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('агрегує стан; джерело без flush — невдача, повторна реєстрація заміщує', async () => {
    offs.push(registerUnsavedSource('test', { hasUnsaved: () => true }));
    expect(hasUnsavedChanges()).toBe(true);
    expect(unsavedCount()).toBe(1);
    expect(await flushUnsaved(10)).toBe(false);

    const staleOff = offs[0];
    registerUnsavedSource('test', { hasUnsaved: () => false });
    staleOff?.(); // відписка старого екземпляра не знімає нового
    expect(hasUnsavedChanges()).toBe(false);
    expect(await flushUnsaved(10)).toBe(true);
  });
});
