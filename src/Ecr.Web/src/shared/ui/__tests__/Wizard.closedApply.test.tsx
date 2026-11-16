import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Wizard, type WizardStep } from '@/shared/ui/Wizard';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';

/**
 * B145b: майстер, що вже ЗАКРИТИЙ (`opened = false`), але ще в DOM, дію не застосовує.
 *
 * ⛔ Дефект класу (той самий, що B145 у `ConfirmModal` / `ReasonModal`). `Wizard` стоїть на `Modal`, який
 * після `opened = false` лишається в DOM ~200 мс (анімація закриття). Власник у цю мить дію вже завершив
 * (`api.close()` після відповіді сервера) і зайнятість зняв, тож другий клік подвійного кліку по `Apply`
 * викликав `onApply` ВДРУГЕ — для майстрів «створити» це другий запит на створення. Захист — у самому
 * `Wizard` (корінь для всіх його користувачів): `apply` нічого не робить, коли майстер закритий.
 *
 * ⚠ Без залежності від часу. `Wizard` справжній; тема `keepMountedModalsTheme` лише лишає закритий
 * `Modal` у DOM назавжди. `isConnected` звіряється перед кожним кліком по закритому майстру.
 *
 * Мутаційний доказ: прибрати `if (!opened) return;` з `apply` у `Wizard.tsx` → червоний
 * (`expected "vi.fn()" to be called 1 times, but got 3 times`).
 */

interface Draft {
  readonly name: string;
}

const Labels = { back: 'Back', next: 'Next', review: 'Review', cancel: 'Cancel' } as const;

const Steps: readonly WizardStep<Draft>[] = [{ id: 'name', label: 'Details', render: () => <span>step</span> }];

afterEach(() => {
  cleanup();
});

describe('B145b: закритий майстер (ще в DOM) не застосовує', () => {
  it('відкритий застосовує раз; закритий — ні; відкритий знову — застосовує', () => {
    const onApply = vi.fn();
    const wizard = (opened: boolean): JSX.Element => (
      <MantineProvider theme={keepMountedModalsTheme}>
        <Wizard<Draft>
          opened={opened}
          title="New permit type"
          initialData={{ name: 'Water' }}
          steps={Steps}
          summary={(data) => data.name}
          applyLabel="Create type"
          labels={Labels}
          onApply={onApply}
          onClose={() => undefined}
          onExitUnsaved={() => true}
        />
      </MantineProvider>
    );
    const view = render(wizard(true));

    fireEvent.click(screen.getByTestId('wizard-next'));
    const button = screen.getByTestId('wizard-apply');
    fireEvent.click(button);
    expect(onApply).toHaveBeenCalledTimes(1);

    // Власник закрив майстра: `opened = false`, кнопка `Apply` ще в DOM і не вимкнена.
    view.rerender(wizard(false));
    expect(button.isConnected).toBe(true);
    expect(button).toHaveProperty('disabled', false);
    fireEvent.click(button);
    fireEvent.click(button);
    expect(onApply).toHaveBeenCalledTimes(1);

    // Відкрили знову (майстер починає з першого кроку) — застосування працює: захист не «залипає».
    view.rerender(wizard(true));
    fireEvent.click(screen.getByTestId('wizard-next'));
    const again = screen.getByTestId('wizard-apply');
    expect(again.isConnected).toBe(true);
    fireEvent.click(again);
    expect(onApply).toHaveBeenCalledTimes(2);
  });
});
