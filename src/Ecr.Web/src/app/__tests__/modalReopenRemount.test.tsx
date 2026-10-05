import { useEffect, useState, type JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Drawer, MantineProvider, Modal, TextInput } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import { withTestDefaults } from '@/test/render';

/**
 * Живий дефект: повторне відкриття діалогу протягом кадру-двох після закриття перемонтовувало
 * його вміст посеред вводу — поле відʼєднувалось із частиною набраного тексту, решта губилась
 * (знайдено в `RegistryEntryEditor.reopenClearsForm.test.tsx`: `expected 'TO' to be 'TOLUENE'`).
 *
 * ⛔ Причина — у Mantine до 7.17.8 (`Transition/use-transition`): нове відкриття скасовувало лише
 * таймер переходу, але не запланований `requestAnimationFrame` закриття. Ланцюжок закриття
 * доходив до кінця вже ПІСЛЯ відкриття, ставив свій таймер «exited», і той спрацьовував за
 * тривалість переходу: вміст розмонтовувався, а таймер «entered» відкриття монтував його знову.
 *
 * Фікс — латка `scripts/patch-mantine-transition.mjs` (те саме, що апстрім зробив у 7.17.8:
 * новий перехід скасовує й запланований кадр попереднього).
 * ⛔ Мутація «без латки» (`npm ci --ignore-scripts`) — обидва тести (`Modal`, `Drawer`) червоні:
 * `expected 2 to be 1`.
 *
 * ⚠ Тема — справжня (`transitionProps.duration` 150 мс для `Modal`/`Drawer`): з нульовою
 * тривалістю Mantine оминає `useTransition` і дефект не відтворюється.
 */

let mounts = 0;

function Content(): JSX.Element {
  useEffect(() => {
    mounts += 1;
  }, []);
  return <TextInput label="code" />;
}

function Harness({ kind }: { kind: 'modal' | 'drawer' }): JSX.Element {
  const [opened, setOpened] = useState(true);
  const Dialog = kind === 'modal' ? Modal : Drawer;

  // Друге перемикання — через один кадр після першого: ланцюжок переходу першого вже почався
  // (`pre-exiting`), а свого таймера ще не поставив. Під навантаженням так буває й
  // між двома діями людини; тут — детерміновано.
  const closeAndReopen = (): void => {
    setOpened(false);
    requestAnimationFrame(() => setOpened(true));
  };

  return (
    <>
      <button type="button" onClick={closeAndReopen}>
        close-reopen
      </button>
      <Dialog opened={opened} onClose={() => setOpened(false)} title="dialog">
        <Content />
      </Dialog>
    </>
  );
}

/**
 * ⛔ Чекання — ПОЗА `act`: усередині `act` React зводить усі оновлення до кінця колбека, і
 * «exited» із «entered» зливались би в один рендер — дефект ховався б саме від тесту, а в
 * браузері кожен таймер рендериться окремо.
 */
async function wait(ms: number): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

/** Чекає кількох кадрів і більше за тривалість переходу (150 мс). */
const settle = (): Promise<void> => wait(500);

afterEach(() => {
  cleanup();
  mounts = 0;
});

describe.each(['modal', 'drawer'] as const)('%s: повторне відкриття одразу після закриття', (kind) => {
  it('вміст не перемонтовується, набране в полі ціле', async () => {
    const user = userEvent.setup();
    render(
      <MantineProvider theme={withTestDefaults(theme)}>
        <Harness kind={kind} />
      </MantineProvider>,
    );
    await settle();
    expect(mounts).toBe(1);

    await user.click(screen.getByRole('button', { name: 'close-reopen', hidden: true }));
    // Кілька кадрів — щоб пастка фокуса діалогу встигла поставити фокус, і друк не змагався з нею;
    // але менше за тривалість переходу: дефектний таймер «exited» ще попереду.
    await wait(60);
    const input = screen.getByLabelText('code');
    await user.type(input, 'TOLUENE');
    await settle();

    // ⛔ ЧЕРВОНИЙ до фіксу: таймер «exited» закриття розмонтовує вміст, mounts == 2, а поле,
    // в яке друкували, вже відʼєднане від документа.
    expect(mounts).toBe(1);
    expect(input.isConnected).toBe(true);
    expect((screen.getByLabelText('code') as HTMLInputElement).value).toBe('TOLUENE');
  });
});
