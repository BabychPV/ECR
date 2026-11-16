import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { ReportDefinitionsModal } from '../ReportDefinitionsModal';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу й вимикає кнопку: під навантаженням CPU тест
// доходив би до другого кліку вже після нього й був би зеленим і без захисту в обробнику. Подвійний клік людини
// швидший за 100 мс, тож тут спінера немає — доказ лише про обробник.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: «Manage report definitions» — менеджер, що лишається відкритим після запису.
 *
 * Зонд (база 826f654e4):
 * - подвійний клік «New version» — ЗАКРИТО: обробник звіряє `addVersion.isPending`, а після успіху поле версії
 *   очищається вже ПІСЛЯ перечитування (запит «в польоті» весь цей час) — повтору немає;
 * - закрите вікно — ВІДКРИТО: власник (`LazyReportDefinitionsModal` на `SnapshotsPage`) тримає вікно змонтованим
 *   (`opened` пропом), форма після закриття на місці, кнопки активні — клік по «New version» закритого `Modal` слав
 *   `POST /api/v1/reports/3/versions` уже після закриття (так само «Add» — `POST /api/v1/reports`).
 *
 * Мутаційний доказ: прибрати `!opened ||` з обробника «New version» у `ReportDefinitionsModal.tsx` → червоний другий
 * тест (`expected [ 'POST /api/v1/reports/3/versions' ] to deeply equal []`).
 */

const Definitions = [
  {
    id: 3,
    code: 'GHG_ANNUAL',
    nameL10n: { values: { en: 'GHG annual' } },
    isRegulatory: false,
    isActive: true,
    versions: [{ id: 30, version: '1.0', status: 'Published', columns: [{ code: 'CO2', kind: 'Decimal' }] }],
  },
] as never;

const AddVersion = 'POST /api/v1/reports/3/versions';

function Host(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <>
      <ReportDefinitionsModal opened={opened} onClose={() => setOpened(false)} definitions={Definitions} />
      <button type="button" onClick={() => setOpened(false)}>
        close
      </button>
    </>
  );
}

async function fillNewVersion(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByRole('textbox', { name: /reportDefs\.forReport/ }));
  fireEvent.click(await screen.findByRole('option', { name: /GHG_ANNUAL/, hidden: true }));
  const versions = screen.getAllByRole('textbox', { name: /reportDefs\.version/ });
  const next = versions[versions.length - 1];
  if (next === undefined) throw new Error('поля версії немає');
  fireEvent.change(next, { target: { value: '2.0' } });
  const buttons = screen.getAllByRole('button', { name: /reportDefs\.newVersion/ });
  const button = buttons[buttons.length - 1];
  if (button === undefined) throw new Error('кнопки «New version» немає');
  await waitFor(() => {
    expect(button.getAttribute('aria-disabled')).toBeNull();
  });

  return button;
}

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: ReportDefinitionsModal — один запис на дію, жодного з закритого вікна', () => {
  it('подвійний клік «New version» шле один POST', async () => {
    const server = holdWrites({ read: () => [] });
    renderKeepMounted(<Host />);
    const button = await fillNewVersion();

    fireEvent.click(button);
    await waitFor(() => {
      expect(server.sent).toEqual([AddVersion]);
    });
    await nextTick();
    fireEvent.click(button);
    await settle();
    expect(server.sent).toEqual([AddVersion]);
  }, 60_000);

  it('«New version» закритого вікна (ще в DOM) нічого не шле', async () => {
    const server = holdWrites({ read: () => [] });
    renderKeepMounted(<Host />);
    const button = await fillNewVersion();

    fireEvent.click(screen.getByRole('button', { name: 'close' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    expect(button.isConnected).toBe(true);
    fireEvent.click(button);
    await settle();
    expect(server.sent).toEqual([]);
  }, 60_000);
});
