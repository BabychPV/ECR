import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import {
  MethodologyBindingsPanel,
  MethodologyRequiredInputsPanel,
} from '@/features/methodologies/MethodologyContentPanels';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { holdReads } from '@/test/heldReads';

/**
 * B149 (група 3, «тіло знімається разом зі станом»): діалоги правки методології (✎ 24.11: лишилися обов'язкові входи й
 * прив'язки — формульні панелі прибрано з інтерфейсу) не шлють `PUT` удруге.
 *
 * Зонд на кожному (справжня панель і діалог, закритий `Modal` лишається в DOM, відповіді віддає тест):
 * - вікно «ще відкритий»: `onSuccess` чекає перечитування переліку панелі ВСЕРЕДИНІ мутації — `isPending`
 *   тримається, клік по «Save» відсікає `if (save.isPending) return`;
 * - після закриття (`editing = null`) тіло `{editing !== null && …}` знімається в тому ж рендері — кнопка
 *   від'єднана. Ціль запиту — знімок `editing` у мить кліку, а не стан на потім.
 * Вердикт: ЗАКРИТО для обох; цей тест — регресія.
 */

const Base = '/api/v1/methodologies/1';
const Version = `${Base}/versions/2`;

interface Case {
  readonly name: string;
  readonly panel: () => JSX.Element;
  readonly list: string;
  readonly item: unknown;
  readonly put: string;
}

const Cases: readonly Case[] = [
  {
    name: "обов'язковий вхід",
    panel: () => <MethodologyRequiredInputsPanel methodologyId={1} versionId={2} editable />,
    list: `${Version}/required-inputs`,
    item: { id: 1, columnDefId: 6017, columnCode: 'Qty', severity: 'Block', hintL10n: null, hasActiveBinding: true },
    put: `PUT ${Version}/required-inputs/6017`,
  },
  {
    name: "прив'язка",
    panel: () => <MethodologyBindingsPanel methodologyId={1} editable />,
    list: `${Base}/bindings`,
    item: {
      id: 1,
      tableDefId: 193,
      tableCode: 'Fuel',
      columnDefId: 6017,
      columnCode: 'Qty',
      outputCode: 'CO2',
      matchJson: '{}',
      isActive: true,
    },
    put: `PUT ${Base}/bindings/6017/CO2`,
  },
];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: діалоги змісту версії методології — один PUT на відкриття', () => {
  it.each(Cases)('$name: ні під час перечитування, ні після закриття другого PUT немає', async (probe) => {
    const server = holdWrites({
      read: (url) => {
        if (url === probe.list) return [probe.item];
        return [];
      },
      write: () => ({ body: probe.item }),
    });
    const reads = holdReads((url) => url === probe.list);
    renderKeepMounted(probe.panel());

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.editFormula⟧' }));
    const save = await screen.findByRole('button', { name: '⟦methodologies.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual([probe.put]);
    });

    // Сервер прийняв; мутація чекає перечитування переліку — діалог ще відкритий.
    reads.arm();
    server.release();
    await waitFor(() => {
      expect(reads.waiting()).toBe(1);
    });
    expect(screen.getByRole('dialog')).not.toBeNull();
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([probe.put]);

    // Перечитано, діалог закрито: тіло знято разом із кнопкою.
    reads.open();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(save.isConnected).toBe(false);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([probe.put]);
  });
});
