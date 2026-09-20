import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiFetch = vi.fn<(path: string, init?: RequestInit) => Promise<unknown>>();
vi.mock('@/api/client', () => ({ apiFetch: (path: string, init?: RequestInit) => apiFetch(path, init) }));

import {
  createNotificationChannel,
  deleteNotificationChannel,
  listNotificationChannels,
  replaceNotificationChannelSecret,
  testNotificationChannel,
  updateNotificationChannel,
} from '../api';

describe('features/notifications/api', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(undefined);
  });

  it('кожна дія йде на свою адресу своїм методом', async () => {
    await listNotificationChannels();
    await createNotificationChannel({ kind: 'TeamsWebhook', name: 'Teams' });
    await updateNotificationChannel(7, { name: 'Teams', isEnabled: false });
    await deleteNotificationChannel(7);
    await testNotificationChannel(7);

    expect(apiFetch.mock.calls.map(([path, init]) => `${init?.method ?? 'GET'} ${path}`)).toEqual([
      'GET /api/v1/notifications/channels',
      'POST /api/v1/notifications/channels',
      'PUT /api/v1/notifications/channels/7',
      'DELETE /api/v1/notifications/channels/7',
      'POST /api/v1/notifications/channels/7/test',
    ]);
    expect(JSON.parse(String(apiFetch.mock.calls[2]?.[1]?.body))).toEqual({ name: 'Teams', isEnabled: false });
  });

  it('секрет їде тілом PUT …/secret, а не адресою', async () => {
    await replaceNotificationChannelSecret(7, 'https://x.logic.azure.com/hook?sig=s3cret');

    const [path, init] = apiFetch.mock.calls[0] ?? [];
    expect(path).toBe('/api/v1/notifications/channels/7/secret');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual({ secret: 'https://x.logic.azure.com/hook?sig=s3cret' });
  });
});
