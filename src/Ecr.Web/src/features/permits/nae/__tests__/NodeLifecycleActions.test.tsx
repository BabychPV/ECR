import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { apiFetch, EcrApiError } from '@/api/client';
import { showApiError, showDone } from '@/shared/ui/notify';
import { renderWithQuery } from '@/test/render';
import { NodeLifecycleActions, referenceKindLabel } from '../NodeLifecycleActions';
import { canRestoreWithAncestors, parentRetiredOf } from '../nodeLifecycle';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, unknown>) => (params === undefined ? key : `${key} ${JSON.stringify(params)}`),
}));
vi.mock('@/shared/ui/notify', () => ({ showApiError: vi.fn(), showDone: vi.fn() }));

/**
 * Закритий вузол NAE (людина 04.11): Restore (з відмовою «закриті предки» і «Restore with parents») і Delete for good
 * (підтвердження; «вживається» - у тому самому діалозі з видами посилань, підтвердження недоступне).
 * Мутаційний доказ (вручну, відновлено): `withAncestors: true` завжди - червоний «перший виклик без предків»;
 * прибраний `confirmDisabled` - червоний «вживаний вузол не підтвердити».
 */

const node = { id: 77, parentId: 5, label: 'Old pump', ordinal: 1, levelCode: 'EQUIP', retired: true };

function conflict(extensions2: Record<string, unknown>): EcrApiError {
  return new EcrApiError({ status: 409, title: 'conflict', detail: 'server text', errorCode: 'ECR-REG-0409', extensions2 } as never);
}

const calls = (): { path: string; method: string | undefined; body: unknown }[] =>
  vi.mocked(apiFetch).mock.calls
    .filter(([path]) => path.includes('/entries/77/'))
    .map(([path, init]) => ({
      path,
      method: init?.method,
      body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined,
    }));

function mount(onPurged = vi.fn()) {
  renderWithQuery(<NodeLifecycleActions registryCode="NAE_TREE" node={node as never} onPurged={onPurged} />);

  return onPurged;
}

afterEach(() => {
  cleanup();
  vi.mocked(apiFetch).mockReset();
  vi.mocked(showDone).mockReset();
  vi.mocked(showApiError).mockReset();
});

describe('nodeLifecycle', () => {
  it('parentRetiredOf: лише відмова parentRetired; видалений предок не дає «разом із предками»', () => {
    const refusal = parentRetiredOf(conflict({ messageKey: 'err.ECR-REG-0409.parentRetired', ancestorIds: [5, 3], deletedAncestorIds: [] }));
    expect(refusal).toEqual({ ancestorIds: [5, 3], deletedAncestorIds: [] });
    expect(canRestoreWithAncestors(refusal!)).toBe(true);
    expect(canRestoreWithAncestors({ ancestorIds: [5, 3], deletedAncestorIds: [3] })).toBe(false);
    expect(parentRetiredOf(conflict({ messageKey: 'err.ECR-REG-0409.entryUsed' }))).toBeNull();
    expect(parentRetiredOf(new Error('x'))).toBeNull();
  });

  it('referenceKindLabel: нові види мають свій ключ; невідомий - «other», не сирий код', () => {
    expect(referenceKindLabel('snapshots')).toBe('registries.referenceKind.snapshots');
    expect(referenceKindLabel('matchRules')).toBe('registries.referenceKind.matchRules');
    expect(referenceKindLabel('cells')).toBe('registries.referenceKind.cells');
    expect(referenceKindLabel('somethingNew')).toBe('registries.referenceKind.other');
  });
});

describe('NodeLifecycleActions', () => {
  it('Restore: POST …/unretire без предків; успіх - сповіщення з кількістю', async () => {
    vi.mocked(apiFetch).mockResolvedValue({ restoredIds: [77], affectedRows: 0 } as never);
    mount();

    fireEvent.click(document.querySelector('[data-nae-restore]')!);

    await waitFor(() => expect(showDone).toHaveBeenCalledWith('permits.nae.lifecycle.restored {"count":"1"}'));
    expect(calls()).toEqual([{ path: '/api/v1/registries/NAE_TREE/entries/77/unretire', method: 'POST', body: { withAncestors: false } }]);
  });

  it('закриті предки: відмова біля кнопки (не сповіщенням) і «Restore with parents» з withAncestors=true', async () => {
    vi.mocked(apiFetch)
      .mockRejectedValueOnce(conflict({ messageKey: 'err.ECR-REG-0409.parentRetired', ancestorIds: [5], deletedAncestorIds: [] }))
      .mockResolvedValueOnce({ restoredIds: [5, 77], affectedRows: 2 } as never);
    mount();

    fireEvent.click(document.querySelector('[data-nae-restore]')!);
    const refused = await waitFor(() => {
      const el = document.querySelector('[data-nae-restore-refused]');
      expect(el).not.toBeNull();

      return el as HTMLElement;
    });
    expect(refused.textContent).toContain('server text');
    expect(showApiError).not.toHaveBeenCalled();

    fireEvent.click(refused.querySelector('[data-nae-restore-with-parents]')!);
    await waitFor(() => expect(showDone).toHaveBeenCalledWith('permits.nae.lifecycle.restored {"count":"2"}'));
    expect(calls().map((c) => c.body)).toEqual([{ withAncestors: false }, { withAncestors: true }]);
    expect(document.querySelector('[data-nae-restore-refused]')).toBeNull();
  });

  it('видалений предок: «Restore with parents» не пропонується', async () => {
    vi.mocked(apiFetch).mockRejectedValueOnce(conflict({ messageKey: 'err.ECR-REG-0409.parentRetired', ancestorIds: [5], deletedAncestorIds: [5] }));
    mount();

    fireEvent.click(document.querySelector('[data-nae-restore]')!);
    await waitFor(() => expect(document.querySelector('[data-nae-restore-refused]')).not.toBeNull());
    expect(document.querySelector('[data-nae-restore-with-parents]')).toBeNull();
  });

  it('Delete for good: підтвердження -> DELETE …/purge -> сповіщення і onPurged', async () => {
    vi.mocked(apiFetch).mockResolvedValue(undefined as never);
    const onPurged = mount();

    fireEvent.click(document.querySelector('[data-nae-purge]')!);
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('permits.nae.lifecycle.deleteText');
    expect(calls()).toEqual([]);
    fireEvent.click(within(dialog).getByRole('button', { name: 'permits.nae.lifecycle.delete' }));

    await waitFor(() => expect(onPurged).toHaveBeenCalled());
    expect(showDone).toHaveBeenCalledWith('permits.nae.lifecycle.deleted');
    expect(calls()).toEqual([{ path: '/api/v1/registries/NAE_TREE/entries/77/purge', method: 'DELETE', body: undefined }]);
  });

  it('вживаний вузол: причина й види посилань у діалозі, підтвердження недоступне, вузол лишається', async () => {
    vi.mocked(apiFetch).mockRejectedValue(
      conflict({ messageKey: 'err.ECR-REG-0409.entryUsed', references: 3, referenceKinds: { snapshots: 2, matchRules: 1 } }),
    );
    const onPurged = mount();

    fireEvent.click(document.querySelector('[data-nae-purge]')!);
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'permits.nae.lifecycle.delete' }));

    const blocked = await waitFor(() => {
      const el = dialog.querySelector('[data-nae-purge-blocked]');
      expect(el).not.toBeNull();

      return el as HTMLElement;
    });
    expect(blocked.textContent).toContain('server text');
    expect(blocked.textContent).toContain('registries.referenceKind.snapshots: 2');
    expect(blocked.textContent).toContain('registries.referenceKind.matchRules: 1');
    expect(within(dialog).getByRole('button', { name: 'permits.nae.lifecycle.delete' })).toHaveProperty('disabled', true);
    expect(showApiError).not.toHaveBeenCalled();
    expect(onPurged).not.toHaveBeenCalled();
  });
});
