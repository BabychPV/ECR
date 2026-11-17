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
 * Мутаційний доказ: прибрати `!opened ||` з обробника «New version» у `ReportDefinitionsModal.tsx` → червоний
 * «New version закритого вікна» (`expected [ 'POST /api/v1/reports/3/versions' ] to deeply equal []`).
 *
 * F13: прибрати `!opened ||` з обробника «Add» → червоний «Add закритого вікна»
 * (`expected [ 'POST /api/v1/reports' ] to deeply equal []`). Повтор після відмови: обробник, що замикається після
 * помилки (`|| create.isError`), → червоний «після відмови…» (один POST замість двох).
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
      <button type="button" onClick={() => setOpened(true)}>
        reopen
      </button>
    </>
  );
}

// ⚠ Код відмови — з каталогу (`ECR-REQ-0422`, `err.ECR-REQ-0422.malformedRequest`).
const Refused = {
  status: 422,
  body: {
    title: 'Unprocessable',
    status: 422,
    errorCode: 'ECR-REQ-0422',
    correlationId: 'cid-refused',
    messageKey: 'err.ECR-REQ-0422.malformedRequest',
  },
};

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

const AddDefinition = 'POST /api/v1/reports';

/** Читання з реєстром мов: без нього `LocalizedInput` не малює поля назви, і «Add» лишається заблокованою. */
function readWithLanguages(url: string): unknown {
  if (url.endsWith('/api/v1/languages')) return [{ code: 'en', nameNative: 'English', isDefault: true, isActive: true }];

  return [];
}

/** Заповнити форму нового опису: «Add» стає доступною. */
async function fillAdd(): Promise<HTMLElement> {
  fireEvent.change(screen.getAllByRole('textbox', { name: /reportDefs\.code/ })[0]!, { target: { value: 'NEW_REPORT' } });
  fireEvent.change(await screen.findByRole('textbox', { name: /registries\.name.*English/ }), { target: { value: 'New report' } });
  fireEvent.change(screen.getAllByRole('textbox', { name: /reportDefs\.version/ })[0]!, { target: { value: '1.0' } });
  fireEvent.change(screen.getByRole('textbox', { name: /reportDefs\.columnCode/ }), { target: { value: 'CO2' } });
  await waitFor(() => {
    const add = screen.getByRole('button', { name: /reportDefs\.add⟧/ });
    expect(add.getAttribute('aria-disabled')).toBeNull();
  });

  // ⚠ `DisabledReason` перемонтовує кнопку, коли причина зникає: шукати її після цього.
  return screen.getByRole('button', { name: /reportDefs\.add⟧/ });
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

  it('«Add» закритого вікна (ще в DOM) нічого не шле', async () => {
    const server = holdWrites({ read: readWithLanguages });
    renderKeepMounted(<Host />);
    const button = await fillAdd();

    fireEvent.click(screen.getByRole('button', { name: 'close' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    expect(button.isConnected).toBe(true);
    expect(button.getAttribute('aria-disabled')).toBeNull();
    fireEvent.click(button);
    await settle();
    expect(server.sent).toEqual([]);
  }, 60_000);

  it('закрити й відкрити знову: «Add» відкритого вікна шле рівно один POST', async () => {
    const server = holdWrites({ read: readWithLanguages });
    renderKeepMounted(<Host />);
    await fillAdd();

    fireEvent.click(screen.getByRole('button', { name: 'close' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    fireEvent.click(screen.getByRole('button', { name: 'reopen' }));
    await screen.findByRole('dialog');

    // Форма менеджера переживає закриття (стан у компоненті) — після повторного відкриття «Add» доступна одразу.
    const add = screen.getByRole('button', { name: /reportDefs\.add⟧/ });
    expect(add.getAttribute('aria-disabled')).toBeNull();
    fireEvent.click(add);
    await waitFor(() => {
      expect(server.sent).toEqual([AddDefinition]);
    });
    await nextTick();
    fireEvent.click(add);
    await settle();
    expect(server.sent).toEqual([AddDefinition]);
    server.release();
  }, 60_000);

  it('після відмови сервера повторний «Add» шле POST знову', async () => {
    const server = holdWrites({ read: readWithLanguages, write: (_m, _u, index) => (index === 0 ? Refused : { body: {} }) });
    renderKeepMounted(<Host />);
    const add = await fillAdd();

    fireEvent.click(add);
    await waitFor(() => {
      expect(server.sent).toEqual([AddDefinition]);
    });
    server.release();
    // Відмова доходить до мутації через розбір відповіді й `notifyManager` (кілька тіків і таймерів), а видимого
    // сліду в діалозі немає (тост `showApiError`) — тому пауза в реальному часі, як у вікні 3 `probeThreeWindows`.
    await act(async () => {
      await new Promise<void>((resolve) => setTimeout(resolve, 300));
    });
    // Відмова не закриває менеджер і не скидає форму: повтор — той самий натиск.
    expect(screen.queryByRole('dialog')).not.toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /reportDefs\.add⟧/ }));
    await waitFor(() => {
      expect(server.sent).toEqual([AddDefinition, AddDefinition]);
    });
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
