import { act, useState, type JSX, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Router, parsePath, useLocation, type To } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { draftOf } from '../../draftModel';
import { TypeEditorContext, type TypeEditorApi } from '../../editorContext';
import { DialogsHost } from '../DialogsHost';
import { permitType } from './fixtures';

/**
 * Хост діалогів на справжніх `DialogsHost` + `TypeEditorDialogs`; роутер — «з відставанням»: запис адреси
 * (`navigate`) лише ставиться в чергу, а зафіксований `location` (його читає `useSearchParams` і рендер) оновлюється,
 * коли тест скаже `commit()`. Так відтворено продакшен, де навігація react-router іде через `startTransition`
 * і `latestParams` відстає від кроку, у якому вже кликнуто «Add table».
 *
 * Мутаційний доказ (виконано, відновлено): повернути в `TypeEditorDialogs` `setTimeout(() => onSelectTable(…), 0)`,
 * а в `DialogsHost.onSelectTable` — запис `new URLSearchParams(latestParams.current)` → `set(t)` → `setParams`:
 * «вибір таблиці не повертає ?dialog=» червоніє (`?dialog=add-table&t=Storage`).
 */
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.EditDefinition', 'Registry.Publish', 'Permit.EditType', 'Permit.PublishType'], isSimulation: false } }),
}));

const lag = vi.hoisted(() => ({ writes: [] as string[], commit: (): void => undefined }));

function LaggingRouter({ initial, children }: { initial: string; children: ReactNode }): JSX.Element {
  const [search, setSearch] = useState(initial);
  lag.commit = () => {
    const next = lag.writes.shift();
    if (next !== undefined) setSearch(next);
  };
  const write = (to: To): void => {
    const target = typeof to === 'string' ? parsePath(to) : to;
    lag.writes.push(target.search ?? '');
  };
  const navigator = {
    createHref: (to: To) => (typeof to === 'string' ? to : `${to.pathname ?? ''}${to.search ?? ''}`),
    go: () => undefined,
    push: write,
    replace: write,
  };

  return (
    <Router location={{ pathname: '/x', search }} navigator={navigator}>
      {children}
    </Router>
  );
}

function Probe(): JSX.Element {
  return <output data-testid="search">{useLocation().search}</output>;
}

/** Фіксує всі записи, що стоять у черзі, по одному; після кожного рендер може поставити нові. */
function commitAll(): void {
  let guard = 20;
  while (lag.writes.length > 0 && guard-- > 0) act(() => lag.commit());
}

function mountHost(entry: string) {
  const type = permitType();
  const api: TypeEditorApi = {
    code: type.code,
    type,
    draft: draftOf(type),
    editable: true,
    save: { kind: 'idle' },
    problems: [],
    saveError: null,
    apply: vi.fn(),
    flush: vi.fn(() => Promise.resolve(true)),
    acceptServer: vi.fn(),
    retry: vi.fn(),
    reload: vi.fn(() => Promise.resolve()),
  };
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <LaggingRouter initial={entry}>
          <TypeEditorContext.Provider value={api}>
            <DialogsHost />
          </TypeEditorContext.Provider>
          <Probe />
        </LaggingRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return { api, ...view };
}

async function readyToApply() {
  const user = userEvent.setup();
  const mounted = mountHost('?dialog=add-table');
  await user.type(await screen.findByLabelText(/addTable\.nameEn/), 'Storage');
  await user.click(screen.getByTestId('wizard-next'));
  await user.click(screen.getByTestId('wizard-next'));

  return mounted;
}

beforeEach(() => {
  lag.writes.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(() => Promise.resolve(new Response(JSON.stringify([]), { status: 200, headers: { 'Content-Type': 'application/json' } }))),
  );
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('DialogsHost: вибір щойно доданої таблиці й адреса', () => {
  it('вибір таблиці не повертає ?dialog= (зафіксована адреса відстає від кліку)', async () => {
    const { api } = await readyToApply();
    vi.useFakeTimers();

    fireEvent.click(screen.getByTestId('wizard-apply'));
    // Старий код робив запис у наступному такті зі СТАРИХ параметрів; даємо цьому такту відбутися до фіксації адреси.
    act(() => {
      vi.advanceTimersByTime(50);
    });
    commitAll();

    expect(api.apply).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('search').textContent).toBe('?t=Storage');
    expect(screen.queryByTestId('wizard-apply')).toBeNull();
  });

  it('хост зник до фіксації закриття — відкладений вибір не пише в чужу адресу', async () => {
    const { unmount } = await readyToApply();

    fireEvent.click(screen.getByTestId('wizard-apply'));
    const queued = [...lag.writes];
    unmount();
    commitAll();

    expect(queued).toEqual(['']);
    expect(lag.writes).toEqual([]);
  });
});
