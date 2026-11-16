import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { RegistrySyncPolicyModal } from '../RegistrySyncPolicyModal';

/**
 * B145b (KIT §6.9): діалог політики синхронізації довідника, що вже закрився, не зберігає політику вдруге — один
 * `PUT …/registry/policy` на відкриття.
 *
 * ⚠ Зонд (попередній агент, база d618ea1f6): у продукті ЗАКРИТО — єдиний власник (`SourceEntitiesTab`) монтує
 * діалог умовно, тож після закриття його немає в дереві. Сам діалог — ВІДКРИТО: якщо власник лишає його в дереві,
 * власний `Modal` ще ~200 мс у DOM і кнопка «Save» слала другий PUT з тією самою політикою. Захист у самому
 * діалозі — другий шар, що не залежить від власника (правило KIT без відомих винятків).
 *
 * Мутаційний доказ: прибрати `!opened ||` у `RegistrySyncPolicyModal.tsx` → червоний
 * (`expected [ …(2) ] to deeply equal [ 'PUT /api/v1/sources/42/registry/policy' ]`).
 */
const Policy = {
  onMissingInSource: 'MarkOrphaned',
  validFromAttribute: null,
  validToAttribute: null,
  validToInclusive: false,
} as const;

function Host({ onSaved }: { readonly onSaved: () => void }): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <RegistrySyncPolicyModal
      entityId={42}
      entityLabel="STACK-1"
      current={Policy}
      opened={opened}
      onClose={() => setOpened(false)}
      onSaved={onSaved}
    />
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: політика синхронізації, діалог що закрився (ще в DOM), не зберігає вдруге', () => {
  it('один PUT; клік по кнопці закритого діалогу нічого не шле', async () => {
    const server = holdWrites({ write: () => ({ body: { id: 42, registrySyncPolicy: Policy } }) });
    const onSaved = vi.fn();
    renderKeepMounted(<Host onSaved={onSaved} />);

    const save = document.querySelector<HTMLButtonElement>('[data-sync-policy-save]');
    if (save === null) throw new Error('кнопки «Save» немає');
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['PUT /api/v1/sources/42/registry/policy']);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(onSaved).toHaveBeenCalledTimes(1);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM.
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PUT /api/v1/sources/42/registry/policy']);
    expect(onSaved).toHaveBeenCalledTimes(1);
  });
});
