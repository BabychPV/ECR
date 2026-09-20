import { useState, type JSX, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider, TextInput } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { Wizard, type WizardStep } from '@/shared/ui/Wizard';

/**
 * `Wizard` (директива №15 §2, шар 3; `KIT.md:374-376`).
 *
 * ⛔ Перевіряється ПОВЕДІНКА, а не наявність вузлів. Тест, який монтує
 * майстер і бачить три `<div>`, лишиться зеленим над майстром, що пускає далі
 * з порожнім обов'язковим полем, губить введене на «Назад» і закривається
 * мовчки — тобто над усім, заради чого цей компонент і написаний.
 *
 * ⚠ Стелі часу названі явно, а не підібрані «щоб не падало»: рендер Mantine
 * у jsdom коштує реального часу (див. `vite.config.ts` про `vmThreads` і
 * `src/test/setup.ts` про рекурсію `nwsapi` на `:modal`). Заміряно на цій
 * машині: найдовший опис тут — «Apply перевіряє ВСІ кроки» — ~2.5 с.
 */

interface Draft {
  readonly name: string;
  readonly scope: string;
}

const Labels = { back: 'Back', next: 'Next', review: 'Review', cancel: 'Cancel' } as const;

const EmptyDraft: Draft = { name: '', scope: '' };

/** Два змістові кроки; `Review` майстер додає САМ (`KIT.md:375`). */
function twoSteps(): readonly WizardStep<Draft>[] {
  return [
    {
      id: 'name',
      label: 'Details',
      hint: 'Shown in the list of versions.',
      render: ({ data, update }) => (
        <TextInput
          id="field-name"
          label="Name"
          value={data.name}
          onChange={(event) => update({ name: event.currentTarget.value })}
        />
      ),
      validate: (data) =>
        data.name.trim().length === 0
          ? { message: 'Name is required.', focus: '#field-name' }
          : null,
    },
    {
      id: 'scope',
      label: 'Coverage',
      render: ({ data, update }) => (
        <TextInput
          id="field-scope"
          label="Scope"
          value={data.scope}
          onChange={(event) => update({ scope: event.currentTarget.value })}
        />
      ),
    },
  ];
}

interface HostSpies {
  readonly onApply: ReturnType<typeof vi.fn>;
  readonly onClose: ReturnType<typeof vi.fn>;
  readonly onExitUnsaved: ReturnType<typeof vi.fn>;
  readonly searches: string[];
}

/** Записує пошуковий рядок адреси на кожному рендері (для «стан не в URL»). */
function LocationProbe({ into }: { into: string[] }): null {
  const { search } = useLocation();
  into.push(search);

  return null;
}

function renderWizard(
  options: {
    steps?: readonly WizardStep<Draft>[];
    summary?: (data: Draft) => ReactNode;
    reviewText?: string;
    exitAnswer?: boolean;
    cancelLabel?: string | undefined;
    danger?: boolean;
  } = {},
): HostSpies {
  const onApply = vi.fn();
  const onClose = vi.fn();
  const onExitUnsaved = vi.fn(() => options.exitAnswer ?? true);
  const searches: string[] = [];

  function Host(): JSX.Element {
    const [opened, setOpened] = useState(true);

    return (
      <Wizard<Draft>
        opened={opened}
        title="Publish version 3"
        initialData={EmptyDraft}
        steps={options.steps ?? twoSteps()}
        summary={options.summary ?? ((data) => <span>{`${data.name} / ${data.scope}`}</span>)}
        {...(options.reviewText === undefined ? {} : { reviewText: options.reviewText })}
        applyLabel="Publish version"
        {...(options.danger === undefined ? {} : { danger: options.danger })}
        labels={{ ...Labels, cancel: options.cancelLabel }}
        onApply={onApply}
        onClose={() => {
          onClose();
          setOpened(false);
        }}
        onExitUnsaved={onExitUnsaved}
      />
    );
  }

  render(
    <MantineProvider theme={theme}>
      <MemoryRouter>
        <LocationProbe into={searches} />
        <Host />
      </MemoryRouter>
    </MantineProvider>,
  );

  return { onApply, onClose, onExitUnsaved, searches };
}

/** Пастка фокуса Mantine переносить фокус у `setTimeout`, не синхронно. */
async function focusSettled(): Promise<void> {
  await waitFor(() => {
    expect(document.activeElement).not.toBe(document.body);
  });
}

function currentStepId(): string | null {
  return screen.getByTestId('wizard-step-body').getAttribute('data-step-id');
}

afterEach(() => {
  cleanup();
});

describe('Wizard — крок Review додається САМ', { timeout: 20_000 }, () => {
  it('після останнього описаного кроку йде Review з тим, що ввели', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();

    // ⛔ У `steps` описано ДВА кроки, у смузі їх ТРИ — третій майстер додав
    // сам. Якби його треба було описувати, цей рядок був би `2`.
    expect(
      within(screen.getByTestId('wizard-steps')).getAllByText(/Details|Coverage|Review/),
    ).toHaveLength(3);
    expect(currentStepId()).toBe('name');

    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));

    expect(currentStepId()).toBe('scope');
    await user.type(screen.getByLabelText('Scope'), 'Boilers');
    await user.click(screen.getByTestId('wizard-next'));

    // ⛔ Головне: на Review видно САМЕ введене, а не заголовок «Review» і
    // порожнеча. Підсумок перед застосуванням — єдине, заради чого майстер
    // відрізняється від форми.
    expect(currentStepId()).toBe('review');
    expect(screen.getByTestId('wizard-summary').textContent).toBe('Version 3 / Boilers');

    // Кнопки «Далі» на Review немає — там дієслово застосування.
    expect(screen.queryByTestId('wizard-next')).toBeNull();
    expect(screen.getByTestId('wizard-apply').textContent).toBe('Publish version');
  });

  it('D15-06: порожній підсумок НЕ дає порожньої рамки', async () => {
    const user = userEvent.setup();
    renderWizard({ summary: () => null });

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-next'));

    expect(currentStepId()).toBe('review');
    expect(screen.queryByTestId('wizard-summary')).toBeNull();
  });

  it('дзеркало: підсумок є — рамка є', async () => {
    const user = userEvent.setup();
    renderWizard({ summary: () => <span>Something</span> });

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-next'));

    expect(screen.getByTestId('wizard-summary').textContent).toBe('Something');
  });
});

describe('Wizard — validate не пускає далі й пояснює В КРОЦІ', { timeout: 20_000 }, () => {
  it('порожнє обов’язкове поле лишає на місці й малює банер саме тут', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    await user.click(screen.getByTestId('wizard-next'));

    // ⛔ Не пустило.
    expect(currentStepId()).toBe('name');

    // ⛔ Банер — УСЕРЕДИНІ тіла кроку, а не десь на екрані: тост живе поза
    // діалогом, зникає сам і не має зв'язку з полем, яке треба виправити.
    const banner = screen.getByTestId('wizard-step-error');
    expect(banner.textContent).toContain('Name is required.');
    expect(screen.getByTestId('wizard-step-body').contains(banner)).toBe(true);

    // ⚠ `role="alert"` — читалка озвучує негайно (`Banner`, тон `danger`).
    expect(banner.getAttribute('role')).toBe('alert');
  });

  it('L9: фокус переходить на перше хибне поле кроку', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    await user.click(screen.getByTestId('wizard-next'));

    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByLabelText('Name'));
    });
  });

  it('дзеркало: заповнене поле пускає далі й банера не лишає', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));

    expect(currentStepId()).toBe('scope');
    expect(screen.queryByTestId('wizard-step-error')).toBeNull();
  });

  it('банер не переїжджає на інший крок: «Назад» його знімає', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    await user.click(screen.getByTestId('wizard-next'));
    expect(screen.getByTestId('wizard-step-error')).toBeDefined();

    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-back'));

    expect(currentStepId()).toBe('name');
    expect(screen.queryByTestId('wizard-step-error')).toBeNull();
  });
});

describe('Wizard — Apply перевіряє ВСІ кроки, а не лише останній', { timeout: 20_000 }, () => {
  /**
   * Крок 1 залежить від того, що вводять на кроці 2.
   *
   * ⚠ Сценарій не вигаданий: «назва версії не може збігатися з областю»,
   * «період документа має бути в межах періоду шаблону» — те саме за формою.
   * Крок 1 проходить перевірку в момент, коли його проходять, і ПЕРЕСТАЄ її
   * проходити через два кроки.
   */
  function crossSteps(): readonly WizardStep<Draft>[] {
    return [
      {
        id: 'name',
        label: 'Details',
        render: ({ data, update }) => (
          <TextInput
            id="field-name"
            label="Name"
            value={data.name}
            onChange={(event) => update({ name: event.currentTarget.value })}
          />
        ),
        validate: (data) =>
          data.name === data.scope
            ? { message: 'Name must differ from scope.', focus: '#field-name' }
            : null,
      },
      {
        id: 'scope',
        label: 'Coverage',
        render: ({ data, update }) => (
          <TextInput
            id="field-scope"
            label="Scope"
            value={data.scope}
            onChange={(event) => update({ scope: event.currentTarget.value })}
          />
        ),
      },
    ];
  }

  it('Apply повертає на перший крок, що більше не проходить, із банером і фокусом', async () => {
    const user = userEvent.setup();
    const { onApply } = renderWizard({ steps: crossSteps() });

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'x');
    await user.click(screen.getByTestId('wizard-next'));

    // Тут крок 1 ще проходив: `name` = 'x', `scope` = ''.
    expect(currentStepId()).toBe('scope');
    await user.type(screen.getByLabelText('Scope'), 'x');
    await user.click(screen.getByTestId('wizard-next'));
    expect(currentStepId()).toBe('review');

    await user.click(screen.getByTestId('wizard-apply'));

    // ⛔ Застосування НЕ відбулося, майстер повернувся туди, де проблема.
    expect(onApply).toHaveBeenCalledTimes(0);
    expect(currentStepId()).toBe('name');
    expect(screen.getByTestId('wizard-step-error').textContent).toContain(
      'Name must differ from scope.',
    );

    // ⛔ Фокус — на полі кроку, якого в DOM не було в момент натискання:
    // саме тому постановка фокуса живе в ефекті, а не в обробнику.
    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByLabelText('Name'));
    });
  });

  it('дзеркало: усе проходить — onApply отримує зібрані дані', async () => {
    const user = userEvent.setup();
    const { onApply } = renderWizard({ steps: crossSteps() });

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'x');
    await user.click(screen.getByTestId('wizard-next'));
    await user.type(screen.getByLabelText('Scope'), 'y');
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-apply'));

    expect(onApply).toHaveBeenCalledTimes(1);
    expect(onApply.mock.calls[0]?.[0]).toEqual({ name: 'x', scope: 'y' });
  });
});

describe('Wizard — «Назад» зберігає введене', { timeout: 20_000 }, () => {
  it('повернення на крок 1 показує те саме значення, а не порожнє поле', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));
    await user.type(screen.getByLabelText('Scope'), 'Boilers');
    await user.click(screen.getByTestId('wizard-back'));

    expect(currentStepId()).toBe('name');
    expect(screen.getByLabelText('Name')).toHaveProperty('value', 'Version 3');

    // ⛔ І крок 2 теж не втратив свого: пройти вперед — значення на місці.
    await user.click(screen.getByTestId('wizard-next'));
    expect(screen.getByLabelText('Scope')).toHaveProperty('value', 'Boilers');
  });

  it('D15-06: на першому кроці кнопки «Назад» немає зовсім', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    expect(screen.queryByTestId('wizard-back')).toBeNull();

    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));

    expect(screen.getByTestId('wizard-back')).toBeDefined();
  });
});

describe('Wizard — стан кроків НЕ потрапляє в адресу', { timeout: 20_000 }, () => {
  it('після переходів вперед і назад пошуковий рядок не змінився', async () => {
    const user = userEvent.setup();
    const { searches } = renderWizard();

    await focusSettled();
    const before = window.location.search;

    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-back'));

    expect(currentStepId()).toBe('scope');

    /*
     * ⛔ Обидва боки: і адреса роутера (`useLocation`), і адреса вікна.
     * Перевірка лише одного з них лишилася б зеленою над майстром, що пише
     * крок повз роутер (`history.replaceState`) або, навпаки, лише в
     * пам'ять роутера.
     */
    expect([...new Set(searches)]).toEqual(['']);
    expect(window.location.search).toBe(before);
  });
});

describe('Wizard — L1: одна primary-кнопка в діалозі', { timeout: 20_000 }, () => {
  function filledButtons(): HTMLElement[] {
    return within(screen.getByRole('dialog'))
      .getAllByRole('button')
      .filter((button) => button.getAttribute('data-variant') === 'filled');
  }

  it('на кроці введення — рівно одна (Next)', async () => {
    renderWizard();
    await focusSettled();

    const filled = filledButtons();
    expect(filled).toHaveLength(1);
    expect(filled[0]).toBe(screen.getByTestId('wizard-next'));
  });

  it('на Review — рівно одна (Apply), і вона не додається до Next', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));

    // На проміжному кроці кнопок три (Back, Cancel, Next) — і лише одна filled.
    expect(filledButtons()).toHaveLength(1);

    await user.click(screen.getByTestId('wizard-next'));

    const filled = filledButtons();
    expect(filled).toHaveLength(1);
    expect(filled[0]).toBe(screen.getByTestId('wizard-apply'));
  });

  it('danger фарбує саме кнопку застосування, не додаючи другої primary', async () => {
    const user = userEvent.setup();
    renderWizard({ danger: true });

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));
    await user.click(screen.getByTestId('wizard-next'));

    expect(filledButtons()).toHaveLength(1);
    expect(screen.getByTestId('wizard-apply').getAttribute('data-variant')).toBe('filled');
  });
});

describe('Wizard — вихід із незбереженим', { timeout: 20_000 }, () => {
  it('нічого не введено — закривається одразу, обробника не турбує', async () => {
    const user = userEvent.setup();
    const { onClose, onExitUnsaved } = renderWizard();

    await focusSettled();
    await user.click(screen.getByTestId('wizard-cancel'));

    // ⚠ Питання на порожньому майстрі — та сама звичка відповідати «так» не
    // читаючи, про яку йдеться в `UnsavedGuard.tsx`.
    expect(onExitUnsaved).toHaveBeenCalledTimes(0);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('є введене — питає обробника і повідомляє, де саме стоїть майстер', async () => {
    const user = userEvent.setup();
    const { onClose, onExitUnsaved } = renderWizard();

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-cancel'));

    expect(onExitUnsaved).toHaveBeenCalledTimes(1);
    expect(onExitUnsaved.mock.calls[0]?.[0]).toEqual({
      stepId: 'name',
      stepIndex: 0,
      data: { name: 'Version 3', scope: '' },
    });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('обробник сказав «ні» — майстер ЛИШАЄТЬСЯ відкритим із введеним', async () => {
    const user = userEvent.setup();
    const { onClose, onExitUnsaved } = renderWizard({ exitAnswer: false });

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-cancel'));

    expect(onExitUnsaved).toHaveBeenCalledTimes(1);

    // ⛔ Ось воно: мовчазного закриття немає. Дані на місці.
    expect(onClose).toHaveBeenCalledTimes(0);
    expect(screen.getByLabelText('Name')).toHaveProperty('value', 'Version 3');
  });

  it('Esc іде тим самим шляхом, що й «Скасувати»', async () => {
    const user = userEvent.setup();
    const { onClose, onExitUnsaved } = renderWizard({ exitAnswer: false });

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.keyboard('{Escape}');

    await waitFor(() => {
      expect(onExitUnsaved).toHaveBeenCalledTimes(1);
    });
    expect(onClose).toHaveBeenCalledTimes(0);
  });
});

describe('Wizard — фокус і підписи', { timeout: 20_000 }, () => {
  it('фокус при відкритті — на НАЗВАНОМУ тілі кроку, а не на хрестику', async () => {
    renderWizard();
    await focusSettled();

    const body = screen.getByTestId('wizard-step-body');
    expect(document.activeElement).toBe(body);
    expect(document.activeElement).not.toBe(screen.getByRole('button', { name: 'Close' }));

    // Область названа — читалка оголошує підпис кроку, а не «група».
    const labelId = body.getAttribute('aria-labelledby');
    expect(labelId).not.toBeNull();
    expect(document.getElementById(labelId ?? '')?.textContent).toBe('Details');
  });

  it('поточний крок у смузі позначений aria-current="step" — рівно один', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    const strip = screen.getByTestId('wizard-steps');
    expect(strip.querySelectorAll('[aria-current="step"]')).toHaveLength(1);
    expect(strip.querySelector('[aria-current="step"]')?.textContent).toBe('Details');

    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));

    expect(strip.querySelectorAll('[aria-current="step"]')).toHaveLength(1);
    expect(strip.querySelector('[aria-current="step"]')?.textContent).toBe('Coverage');
  });

  it('D15-06: підказки немає — рядка немає', async () => {
    const user = userEvent.setup();
    renderWizard();

    await focusSettled();
    expect(screen.getByText('Shown in the list of versions.')).toBeDefined();

    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));

    // У кроку `scope` `hint` не заданий.
    expect(screen.queryByText('Shown in the list of versions.')).toBeNull();
  });

  it('підпис «Скасувати» за замовчуванням іде через каталог, а не зашитий рядком', async () => {
    renderWizard({ cancelLabel: undefined });
    await focusSettled();

    /*
     * ⚠ Каталог у компонентному тесті не завантажений, тож `t()` чесно
     * повертає ПОЗНАЧЕНИЙ ключ (`shared/i18n`, `Missing`). Саме це тут і
     * доводиться: підпис береться з ключа `common.cancel` (єдиного
     * придатного, що є в `09-seed.sql`), а не з літерала в коді — літерал
     * дав би тут слово, а не дужки.
     */
    expect(screen.getByTestId('wizard-cancel').textContent).toBe('⟦common.cancel⟧');
  });
});

describe('Wizard — повторне відкриття не тягне попереднє введення', { timeout: 20_000 }, () => {
  function Host(): JSX.Element {
    const [opened, setOpened] = useState(true);

    return (
      <>
        <button type="button" onClick={() => setOpened(true)}>
          Open
        </button>
        <Wizard<Draft>
          opened={opened}
          title="Publish version 3"
          initialData={EmptyDraft}
          steps={twoSteps()}
          summary={(data) => <span>{data.name}</span>}
          applyLabel="Publish version"
          labels={Labels}
          onApply={() => {}}
          onClose={() => setOpened(false)}
          onExitUnsaved={() => true}
        />
      </>
    );
  }

  it('крок і дані скидаються при КОЖНОМУ відкритті', async () => {
    const user = userEvent.setup();
    render(
      <MantineProvider theme={theme}>
        <Host />
      </MantineProvider>,
    );

    await focusSettled();
    await user.type(screen.getByLabelText('Name'), 'Version 3');
    await user.click(screen.getByTestId('wizard-next'));
    expect(currentStepId()).toBe('scope');

    await user.click(screen.getByTestId('wizard-cancel'));
    await waitFor(() => {
      expect(screen.queryByTestId('wizard-step-body')).toBeNull();
    });

    await user.click(screen.getByRole('button', { name: 'Open' }));

    /*
     * ⛔ Тут `focusSettled()` НЕ підходить, і саме на цьому тест червонів на
     * CI, лишаючись зеленим локально. Його умова — «фокус уже не на `body`», а
     * після клацання по «Open» фокус тримає сама кнопка, тобто очікування
     * повертається НЕГАЙНО — ще до того, як Mantine домалює тіло модалки.
     * Локально перемальовування встигає в тому ж такті, на повільнішому
     * агенті — ні, і `getByTestId` падає на порожньому `mantine-Modal-root`.
     *
     * ⚠ Твердження від цього не ослабло: `waitFor` повторює саме ПОРІВНЯННЯ.
     * Якби майстер лишив попередній крок, `currentStepId()` повертав би
     * `'scope'` до самого таймауту — і випадок упав би, як і має.
     */
    await waitFor(() => {
      // ⛔ Інакше параметри ПОПЕРЕДНЬОЇ публікації поїхали б у наступну.
      expect(currentStepId()).toBe('name');
    });

    await focusSettled();
    expect(screen.getByLabelText('Name')).toHaveProperty('value', '');
  });
});

describe('Wizard — помилки опису падають у розробці, а не мовчать', { timeout: 20_000 }, () => {
  /*
   * ⚠ React друкує впійманий виняток рендера в консоль — це шум самого
   * React, а не сигнал тесту; глушиться на час опису, щоб він не читався як
   * падіння.
   */
  let consoleError: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    consoleError = vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    consoleError.mockRestore();
  });

  function mountWith(steps: readonly WizardStep<Draft>[]): void {
    render(
      <MantineProvider theme={theme}>
        <Wizard<Draft>
          opened
          title="Publish version 3"
          initialData={EmptyDraft}
          steps={steps}
          summary={() => null}
          applyLabel="Publish version"
          labels={Labels}
          onApply={() => {}}
          onClose={() => {}}
          onExitUnsaved={() => true}
        />
      </MantineProvider>,
    );
  }

  it('крок із зарезервованим id "review" — виняток, а не другий крок із тим самим іменем', () => {
    expect(() =>
      mountWith([
        { id: 'review', label: 'Mine', render: () => <span>x</span> },
      ]),
    ).toThrow(/review/);
  });

  it('майстер без кроків — виняток із вказівкою на ConfirmDialog', () => {
    expect(() => mountWith([])).toThrow(/ConfirmDialog/);
  });

  it('дзеркало: звичайний опис монтується без винятку', () => {
    expect(() => mountWith(twoSteps())).not.toThrow();
  });
});
