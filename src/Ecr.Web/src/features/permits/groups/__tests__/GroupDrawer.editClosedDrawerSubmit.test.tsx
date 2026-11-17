import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createTheme, MantineProvider, mergeThemeOverrides } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { holdWrites, settle, type HeldWrites } from '@/test/closedDialogProbe';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';
import { GroupDrawer } from '../GroupDrawer';
import type { PermitGroupDto } from '../groupsApi';

/**
 * B149: шторка групи дозволів — шлях ПРАВКИ (створення тримає `GroupDrawer.closedDrawerSubmit.test.tsx`).
 *
 * Зонд (база 826f654e4, справжня шторка, `keepMounted`): ЗАКРИТО. Подвійний сабміт (наступний тік) тримає `busy` у
 * `submit`; сабміт шторки, що закрила себе після успішного `PUT /api/v1/permit-groups/WATER`, нічого не шле вже через
 * сам стан: після успіху `original` дорівнює чернетці, і `submit` лише закриває шторку («нічого не змінено»), а
 * `leaving.current` — другий шар.
 *
 * Мутації (локально): без `busy ||` — червоний перший тест (`expected [ …(2) ] to deeply equal
 * [ 'PUT /api/v1/permit-groups/WATER' ]`); без `leaving.current ||` — зелений (тримає стан «нічого не змінено»).
 */

const theme = mergeThemeOverrides(keepMountedModalsTheme, createTheme({ components: { Drawer: { defaultProps: { keepMounted: true } } } }));

const Group: PermitGroupDto = {
  code: 'WATER',
  descriptionL10n: {},
  isActive: true,
  nameL10n: { en: 'Water' },
  registryCode: 'PRMG_WATER',
  registryDefId: 90,
  rowVersion: 'v1',
  types: [],
  updatedAt: '2026-10-20T10:00:00Z',
  usedInColumns: 0,
  usedInTemplates: 0,
} as unknown as PermitGroupDto;

const Update = 'PUT /api/v1/permit-groups/WATER';

function read(url: string): unknown {
  if (url.includes('/languages')) return [{ code: 'en', nameNative: 'English', isDefault: true }];
  if (url.includes('/permit-types')) return { items: [], nextCursor: null, totalCount: 0 };

  return [];
}

async function openEdited(): Promise<{ server: HeldWrites; form: HTMLFormElement }> {
  const server = holdWrites({ read, write: () => ({ body: { ...Group, nameL10n: { en: 'Water bodies' }, rowVersion: 'v2' } }) });
  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/?panel=WATER']}>
          <GroupDrawer group={Group} canEdit />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.change(await screen.findByLabelText(/permits\.groups\.field\.name.*English/), { target: { value: 'Water bodies' } });
  const form = document.getElementById('permit-group-form');
  if (!(form instanceof HTMLFormElement)) throw new Error('форми шторки немає');

  return { server, form };
}

/** Наступний тік: `busy` уже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: шторка групи — правка: один PUT на збереження', () => {
  it('подвійний сабміт правки шле один PUT', async () => {
    const { server, form } = await openEdited();

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toEqual([Update]);
    });
    await nextTick();
    fireEvent.submit(form);
    await settle();
    expect(server.sent).toEqual([Update]);
  });

  it('сабміт шторки, що закрила себе після збереження (ще в DOM), нічого не шле', async () => {
    const { server, form } = await openEdited();

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toEqual([Update]);
    });
    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    await settle();

    expect(form.isConnected).toBe(true);
    fireEvent.submit(form);
    await settle();
    expect(server.sent).toEqual([Update]);
  });
});
