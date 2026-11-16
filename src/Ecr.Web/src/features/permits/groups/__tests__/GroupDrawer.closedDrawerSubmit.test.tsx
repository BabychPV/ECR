import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createTheme, MantineProvider, mergeThemeOverrides } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { holdWrites, settle, type HeldReply, type HeldWrites } from '@/test/closedDialogProbe';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';
import { GroupDrawer } from '../GroupDrawer';
import { NewGroupPanel } from '../groupsModel';

/**
 * B145b: шторка «New group», що вже закрила себе, не створює групу вдруге — один POST на збереження.
 *
 * ⛔ Діра (зонд на базі 812a049c9, справжня сторінка `PermitGroupsPage`): між відповіддю сервера і зняттям шторки з
 * дерева (сторінка знімає її за `?panel=`) форма ще в DOM, `busy` уже `false`, код нової групи на місці — другий
 * сабміт слав другий `POST /api/v1/permit-groups` з тим самим тілом (сервер відповів би `409 ECR-PRM-4097` — помилка
 * після успішного створення). Подвійний сабміт ДО відповіді закритий і на базі (`busy`) — перший тест лише тримає це.
 *
 * ⚠ Тут шторка лишається в DOM після закриття назавжди (`keepMounted` у темі тесту): вікно зі сторінки — кілька
 * тіків без опори на час, а цей тест від порядку тіків не залежить.
 *
 * Мутаційний доказ: прибрати `leaving.current ||` у `GroupDrawer.submit` → червоний другий тест
 * (`expected [ 'POST /api/v1/permit-groups', …(1) ] to deeply equal [ 'POST /api/v1/permit-groups' ]`).
 */

const theme = mergeThemeOverrides(keepMountedModalsTheme, createTheme({ components: { Drawer: { defaultProps: { keepMounted: true } } } }));

const Create = 'POST /api/v1/permit-groups';

const Created = {
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
};

// ⚠ Код і ключ відмови — з каталогу (`PERMITS-groups-api.sql`).
const Duplicate = {
  status: 409,
  body: { title: 'Conflict', status: 409, errorCode: 'ECR-PRM-4097', correlationId: 'c-1', messageKey: 'err.ECR-PRM-4097.code', detail: 'Code is taken.' },
};

function read(url: string): unknown {
  if (url.includes('/languages')) return [{ code: 'en', nameNative: 'English', isDefault: true }];
  if (url.includes('/permit-types')) return { items: [], nextCursor: null, totalCount: 0 };

  return [];
}

async function openFilled(write: (method: string, url: string, index: number) => HeldReply): Promise<{ server: HeldWrites; form: HTMLFormElement }> {
  const server = holdWrites({ read, write });
  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={[`/?panel=${NewGroupPanel}`]}>
          <GroupDrawer group={null} canEdit />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.change(await screen.findByLabelText(/permits\.groups\.field\.code/), { target: { value: 'WATER' } });
  fireEvent.change(await screen.findByLabelText(/permits\.groups\.field\.name.*English/), { target: { value: 'Water' } });
  const form = document.getElementById('permit-group-form');
  if (!(form instanceof HTMLFormElement)) throw new Error('форми шторки немає');

  return { server, form };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: New group — один POST на збереження', () => {
  it('подвійний сабміт до відповіді шле один POST', async () => {
    const { server, form } = await openFilled(() => ({ status: 201, body: Created }));

    fireEvent.submit(form);
    fireEvent.submit(form);
    await settle();

    expect(server.sent).toEqual([Create]);
  });

  it('сабміт шторки, що закрила себе (ще в DOM), нічого не шле', async () => {
    const { server, form } = await openFilled(() => ({ status: 201, body: Created }));

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toEqual([Create]);
    });
    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    await settle();

    expect(form.isConnected).toBe(true);
    fireEvent.submit(form);
    await settle();
    expect(server.sent).toEqual([Create]);
  });

  it('відмова сервера звільняє збереження: шторка відкрита, повтор шле другий POST', async () => {
    const { server, form } = await openFilled((_method, _url, index) => (index === 0 ? Duplicate : { status: 201, body: Created }));

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toEqual([Create]);
    });
    server.release();
    expect(await screen.findByText(/ECR-PRM-4097/)).not.toBeNull();
    expect(screen.queryByRole('dialog')).not.toBeNull();

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toEqual([Create, Create]);
    });
  });
});
