import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { EcrApiError } from '@/api/client';

const api = vi.hoisted(() => ({
  publishPermitType: vi.fn(),
  openPermitTypeVersion: vi.fn(),
  deletePermitType: vi.fn(),
}));
const notify = vi.hoisted(() => ({ showApiError: vi.fn(), showDone: vi.fn() }));

vi.mock('@/features/permits/api/typesApi', () => api);
vi.mock('@/shared/ui/notify', () => notify);

import { useTypeLifecycle } from '../useTypeLifecycle';
import { permitType } from './fixtures';

/** Мутації життєвого циклу (PM-08c): виклики API, скидання кешу, відмови 0422/4093/4094. */

function problem(errorCode: string, status: number): EcrApiError {
  return new EcrApiError({
    title: errorCode,
    status,
    errorCode,
    correlationId: 'c1',
    extensions2: { messageKey: `err.${errorCode}.x` },
  });
}

function setup() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  const handlers = {
    onPublished: vi.fn(),
    onVersionOpened: vi.fn(),
    onDeleted: vi.fn(),
    onConflict: vi.fn(),
  };
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  const { result } = renderHook(() => useTypeLifecycle(permitType(), handlers), { wrapper });

  return { result, invalidate, handlers };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('useTypeLifecycle', () => {
  it('публікація: тіло {reason,rowVersion}, скидання [permits,types], тост, onPublished', async () => {
    const next = permitType({ status: 'Published', version: 1, draftVersion: null });
    api.publishPermitType.mockResolvedValue(next);
    const { result, invalidate, handlers } = setup();

    act(() => {
      result.current.publish.mutate({ reason: 'Added the storage table', rowVersion: 'RV1' });
    });

    await waitFor(() => {
      expect(handlers.onPublished).toHaveBeenCalledWith(next);
    });
    expect(api.publishPermitType).toHaveBeenCalledWith('WaterDischarge', {
      reason: 'Added the storage table',
      rowVersion: 'RV1',
    });
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['permits', 'types'] });
    expect(notify.showDone).toHaveBeenCalledTimes(1);
  });

  it('публікація, ECR-PRM-4093: тост відмови й onConflict, onPublished не викликано', async () => {
    const error = problem('ECR-PRM-4093', 409);
    api.publishPermitType.mockRejectedValue(error);
    const { result, handlers, invalidate } = setup();

    act(() => {
      result.current.publish.mutate({ reason: 'Added the storage table', rowVersion: 'RV1' });
    });

    await waitFor(() => {
      expect(handlers.onConflict).toHaveBeenCalledTimes(1);
    });
    expect(notify.showApiError).toHaveBeenCalledWith(error);
    expect(handlers.onPublished).not.toHaveBeenCalled();
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('публікація, ECR-PRM-0422: лише тост відмови, без onConflict', async () => {
    const error = problem('ECR-PRM-0422', 422);
    api.publishPermitType.mockRejectedValue(error);
    const { result, handlers } = setup();

    act(() => {
      result.current.publish.mutate({ reason: 'Added the storage table', rowVersion: 'RV1' });
    });

    await waitFor(() => {
      expect(notify.showApiError).toHaveBeenCalledWith(error);
    });
    expect(handlers.onConflict).not.toHaveBeenCalled();
    expect(handlers.onPublished).not.toHaveBeenCalled();
  });

  it('нова версія: виклик API, скидання кешу, onVersionOpened', async () => {
    const next = permitType({ status: 'Published', version: 1, draftVersion: 2 });
    api.openPermitTypeVersion.mockResolvedValue(next);
    const { result, handlers, invalidate } = setup();

    act(() => {
      result.current.openVersion.mutate();
    });

    await waitFor(() => {
      expect(handlers.onVersionOpened).toHaveBeenCalledWith(next);
    });
    expect(api.openPermitTypeVersion).toHaveBeenCalledWith('WaterDischarge');
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['permits', 'types'] });
  });

  it('нова версія, ECR-PRM-4093 (чернетка вже є): onConflict', async () => {
    api.openPermitTypeVersion.mockRejectedValue(problem('ECR-PRM-4093', 409));
    const { result, handlers } = setup();

    act(() => {
      result.current.openVersion.mutate();
    });

    await waitFor(() => {
      expect(handlers.onConflict).toHaveBeenCalledTimes(1);
    });
  });

  it('видалення: виклик API, onDeleted; відмова 4094 — тост, тип лишається', async () => {
    api.deletePermitType.mockResolvedValueOnce(undefined);
    const ok = setup();
    act(() => {
      ok.result.current.remove.mutate();
    });
    await waitFor(() => {
      expect(ok.handlers.onDeleted).toHaveBeenCalledTimes(1);
    });
    expect(api.deletePermitType).toHaveBeenCalledWith('WaterDischarge');

    const error = problem('ECR-PRM-4094', 409);
    api.deletePermitType.mockRejectedValueOnce(error);
    const failed = setup();
    act(() => {
      failed.result.current.remove.mutate();
    });
    await waitFor(() => {
      expect(notify.showApiError).toHaveBeenCalledWith(error);
    });
    expect(failed.handlers.onDeleted).not.toHaveBeenCalled();
    expect(failed.handlers.onConflict).not.toHaveBeenCalled();
  });
});
