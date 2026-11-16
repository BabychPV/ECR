import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { AddRegionModal } from '../AddRegionModal';

/**
 * B145b: «Add region», що вже закрився, не створює регіон удруге — один POST на відкриття.
 *
 * ⚠ Зонд (база 812a049c9): на сторінці `NaeStructurePage` ЗАКРИТО — власник знімає діалог із дерева одразу
 * (`{addRegionOpen && …}`), кнопка від'єднана, один POST. Сам діалог — ВІДКРИТО: якщо власник лишає його в дереві
 * (як будь-який діалог з анімацією закриття), власний `Modal` ще ~200 мс у DOM, регіон обраний, кнопка активна, і
 * другий клік слав другий `POST /api/v1/nae-tree/roots` з тим самим тілом.
 *
 * Захист — у самому діалозі (другий шар, не залежить від власника): `submit` діє лише у відкритому діалозі й не під
 * час запиту.
 *
 * Мутаційний доказ: прибрати `!opened ||` у `AddRegionModal.tsx` → червоний
 * (`expected [ …(2) ] to deeply equal [ 'POST /api/v1/nae-tree/roots' ]`).
 */

function read(url: string): unknown {
  if (url.includes('/registries/LAND_REGION/rows')) {
    return { items: [{ id: 1, code: 'KZ-ATY', display: 'Atyrau' }], nextCursor: null, totalCount: 1 };
  }
  if (url.includes('/rows')) return { items: [], nextCursor: null, totalCount: 0 };
  if (/\/entries\/\d+$/.test(url)) return { id: 1, code: 'KZ-ATY', displayL10n: { values: { en: 'Atyrau' } } };

  return [];
}

function Host({ onAdded }: { readonly onAdded: (nodeId: number) => void }): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <AddRegionModal
      opened={opened}
      onClose={() => setOpened(false)}
      onAdded={(nodeId) => {
        setOpened(false);
        onAdded(nodeId);
      }}
    />
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: Add region, що закрився (ще в DOM), не створює вдруге', () => {
  it('один POST; клік по кнопці закритого діалогу нічого не шле', async () => {
    const server = holdWrites({
      read,
      write: () => ({ body: { created: true, regionEntryId: 5, areaEntryId: null, registryCode: 'NAE_TREE', registryProvisioned: false } }),
    });
    const onAdded = vi.fn();
    renderKeepMounted(<Host onAdded={onAdded} />);

    const submit = document.querySelector<HTMLButtonElement>('[data-nae-add-region-submit]');
    if (submit === null) throw new Error('кнопки «Add region» немає');
    await waitFor(() => {
      expect(document.querySelector<HTMLSelectElement>('[data-nae-region]')?.options.length).toBe(1);
    });
    fireEvent.click(submit);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/nae-tree/roots']);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(submit).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(submit.isConnected).toBe(true);
    fireEvent.click(submit);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/nae-tree/roots']);
    expect(onAdded).toHaveBeenCalledTimes(1);
    expect(onAdded).toHaveBeenCalledWith(5);
  });
});
