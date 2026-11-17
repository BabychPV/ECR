import { afterEach, describe, it, vi } from 'vitest';
import { fireEvent, screen, within } from '@testing-library/react';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «null-захист в обробнику»: форма каналу (`draft`) і заміна адреси вебхука (`secretFor`), що
 * закрилися (ще в DOM) або ще чекають перечитування переліку каналів, не шлють запис удруге.
 *
 * Зонд (справжня панель, `keepMountedModalsTheme`, запис тримає тест, перечитування — ворота):
 * - New channel — вікна 1–2: `if (save.isPending) return` (спінер `usePendingLoading` ще вимкнений); вікно 3:
 *   тіло діалогу живе лише при `draft !== null` — кнопка від'єднана. ЗАКРИТО, один `POST …/channels`.
 * - Set webhook — те саме з `saveSecret.isPending` і `secretFor !== null`. ЗАКРИТО, один `PUT …/channels/2/secret`.
 */

const channels = [
  {
    id: 2,
    name: 'Duty channel',
    kind: 'TeamsWebhook',
    isEnabled: true,
    hasSecret: false,
    modifiedAt: '2026-09-02T10:00:00Z',
    settings: { title: 'ECR' },
    transportFromConfiguration: false,
  },
];

function read(url: string): unknown {
  if (url.endsWith('/api/v1/notifications/channels')) return channels;
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 1,
      userName: 'admin',
      language: 'en',
      permissions: ['System.ManageNotifications'],
      isSimulation: false,
      denies: [],
      grants: {},
      mustChangePassword: false,
      simulatedForUserId: null,
    };
  }

  return [];
}

const isOpen = (): boolean => screen.queryByRole('dialog') !== null;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: ChannelsPanel — один запис на відкриття діалогу', () => {
  it('New channel: один POST /notifications/channels у всіх трьох вікнах', async () => {
    const server = holdWrites({ read, write: () => ({ body: { ...channels[0], id: 5, name: 'Ops' } }) });
    const gate = gateReads();
    renderKeepMounted(<ChannelsPanel />);

    fireEvent.click(await screen.findByRole('button', { name: /notifications\.addChannel/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/notifications\.channelName/), { target: { value: 'Ops' } });
    const save = within(dialog).getByRole('button', { name: '⟦common.save⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => save,
      isOpen,
      expected: ['POST /api/v1/notifications/channels'],
      awaitedRead: '/api/v1/notifications/channels',
      afterClose: 'detached',
    });
  });

  it('Set webhook: один PUT …/channels/2/secret у всіх трьох вікнах', async () => {
    const server = holdWrites({ read, write: () => ({ body: { ...channels[0], hasSecret: true } }) });
    const gate = gateReads();
    renderKeepMounted(<ChannelsPanel />);

    fireEvent.click(await screen.findByRole('button', { name: '⟦notifications.setWebhook⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/notifications\.webhookUrl/), {
      target: { value: 'https://example.webhook.office.com/hook' },
    });
    const save = within(dialog).getByRole('button', { name: '⟦common.save⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => save,
      isOpen,
      expected: ['PUT /api/v1/notifications/channels/2/secret'],
      awaitedRead: '/api/v1/notifications/channels',
      afterClose: 'detached',
    });
  });
});
