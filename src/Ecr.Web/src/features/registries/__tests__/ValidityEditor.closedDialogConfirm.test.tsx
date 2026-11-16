import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import type { RegistryEntryDto } from '@/api/types';
import { ValidityEditor } from '@/features/registries/RegistryEntryEditor';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B145b: діалог «Validity» запису довідника, що вже закрився, не шле запит на ІНШИЙ запис.
 *
 * ⛔ Діра (зонд на базі d618ea1f6, справжній діалог): після відповіді сервера власник знімає запис
 * (`entry = null`) — це й закриває діалог, але власний `Modal` ще ~200 мс у DOM і «Save» активна. Адресу
 * запиту кнопка читала з `entry` НА МОМЕНТ КЛІКУ (`entry?.id ?? 0`), тож другий клік подвійного кліку слав
 * `POST …/entries/0/validity` — запит на запис, якого людина не відкривала.
 *
 * Захист — у самому діалозі: кнопка діє лише поки діалог відкритий (`entry !== null`) і не під час запиту.
 *
 * Мутаційний доказ: прибрати `entry === null ||` у `ValidityEditor` → червоний
 * (`expected [ …(2) ] to deeply equal [ 'POST /api/v1/registries/Units/entries/5/validity' ]`).
 */

const Entry = { id: 5, code: 'E5', validFrom: null, validTo: null } as unknown as RegistryEntryDto;

function Host(): JSX.Element {
  const [entry, setEntry] = useState<RegistryEntryDto | null>(Entry);

  return <ValidityEditor registryCode="Units" entry={entry} onClose={() => setEntry(null)} />;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: Validity, що закрився (ще в DOM), не шле другий запит', () => {
  it('один POST на відкритий запис; клік по «Save» закритого діалогу нічого не шле', async () => {
    const server = holdWrites({ write: () => ({ body: { affectedRows: 0 } }) });
    renderKeepMounted(<Host />);

    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/registries/Units/entries/5/validity']);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(save).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/registries/Units/entries/5/validity']);
  });
});
