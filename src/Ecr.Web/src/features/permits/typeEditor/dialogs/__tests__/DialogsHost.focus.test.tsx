import { StrictMode, type JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useSearchParams } from 'react-router-dom';
import { RouteHeadingClass } from '@/shared/theme/routeHeading';
import { testTheme } from '@/test/render';
import { draftOf } from '../../draftModel';
import { TypeEditorContext, type TypeEditorApi } from '../../editorContext';
import { readDialog } from '../../editorActions';
import { DialogsHost } from '../DialogsHost';
import { permitType } from './fixtures';

/**
 * «Delete type…» з меню переліку веде в редактор вже з `?dialog=delete-type`: кнопки-виклику на сторінці немає, тож
 * `useRestoreFocus` нічого не повертає, і після Esc фокус падав на `BODY`. Тепер він іде на заголовок сторінки.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати ефект із `requestAnimationFrame` у `DialogsHost` - перший тест
 * червоніє (фокус лишається на `BODY`).
 */
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.EditDefinition', 'Registry.Publish', 'Permit.EditType', 'Permit.PublishType'], isSimulation: false } }),
}));

function Page(): JSX.Element {
  const [params] = useSearchParams();

  return (
    <>
      <h1 className={RouteHeadingClass} tabIndex={-1}>
        Water discharge
      </h1>
      {readDialog(params) !== null && <DialogsHost />}
    </>
  );
}

function mount(entry: string, strict = false): void {
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
  const tree = (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <TypeEditorContext.Provider value={api}>
            <Page />
          </TypeEditorContext.Provider>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
  render(strict ? <StrictMode>{tree}</StrictMode> : tree);
}

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => Promise.resolve(new Response(JSON.stringify([]), { status: 200, headers: { 'Content-Type': 'application/json' } }))),
  );
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('DialogsHost: фокус після закриття діалогу, відкритого з адреси', () => {
  for (const strict of [false, true]) {
    it(`Esc на delete-type без кнопки-виклику: фокус на заголовку, не на BODY${strict ? ' (StrictMode)' : ''}`, async () => {
      mount('/x?dialog=delete-type', strict);
      const dialog = await screen.findByRole('dialog');
      await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

      fireEvent.keyDown(dialog, { key: 'Escape' });

      await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
      await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Water discharge' })));
    });
  }
});
