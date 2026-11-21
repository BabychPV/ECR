import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { lazy, Suspense } from 'react';
import { loadNotifications } from '../lazyNotifications';

describe('loadNotifications', () => {
  it('відхилений import() не знімає решту застосунку', async () => {
    const importer = vi.fn().mockRejectedValue(new TypeError('Failed to fetch dynamically imported module'));
    const Notifications = lazy(() => loadNotifications(importer));
    render(
      <>
        <Suspense fallback={null}>
          <Notifications />
        </Suspense>
        <div>rest-of-app</div>
      </>,
    );
    await vi.waitFor(() => expect(importer).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('rest-of-app')).toBeTruthy();
  });

  it('одна невдача — друга спроба вдається', async () => {
    const Ok = () => <span>toasts</span>;
    const importer = vi.fn().mockRejectedValueOnce(new Error('blip')).mockResolvedValueOnce({ Notifications: Ok });
    const Notifications = lazy(() => loadNotifications(importer));
    render(
      <Suspense fallback={null}>
        <Notifications />
      </Suspense>,
    );
    expect(await screen.findByText('toasts')).toBeTruthy();
    expect(importer).toHaveBeenCalledTimes(2);
  });
});
