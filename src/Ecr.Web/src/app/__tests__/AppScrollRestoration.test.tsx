import { afterEach, describe, expect, it, vi } from 'vitest';
import type { JSX } from 'react';
import { act, render } from '@testing-library/react';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router-dom';
import { AppScrollRestoration, scrollRestorationKey } from '../AppScrollRestoration';

/**
 * ✎ debt-1109 x7: зміна лише рядка запиту (`?table=`) — не навігація для відновлення прокрутки. Із типовим ключем
 * (`location.key`) `<ScrollRestoration/>` робив `scrollTo(0, 0)` на кожен вибір таблиці в дереві документа.
 */
afterEach(() => {
  vi.restoreAllMocks();
  Object.defineProperty(window, 'scrollY', { value: 0, configurable: true });
});

function Shell(): JSX.Element {
  return (
    <>
      <AppScrollRestoration />
      <Outlet />
    </>
  );
}

describe('AppScrollRestoration', () => {
  it('ключ — шлях без запиту', () => {
    expect(scrollRestorationKey({ pathname: '/documents/5' })).toBe('/documents/5');
  });

  it('зміна лише запиту не скидає прокрутку на (0, 0); інший шлях — скидає', async () => {
    const router = createMemoryRouter(
      [{ path: '/', element: <Shell />, children: [{ path: 'documents/:id', element: <p>doc</p> }, { path: 'other', element: <p>other</p> }] }],
      { initialEntries: ['/documents/5?table=1'] },
    );
    const scrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => undefined);
    render(<RouterProvider router={router} />);

    Object.defineProperty(window, 'scrollY', { value: 640, configurable: true });
    scrollTo.mockClear();
    await act(async () => {
      await router.navigate('/documents/5?table=2', { replace: true });
    });

    expect(scrollTo).not.toHaveBeenCalledWith(0, 0);

    scrollTo.mockClear();
    await act(async () => {
      await router.navigate('/other');
    });

    expect(scrollTo).toHaveBeenCalledWith(0, 0);
  });
});
