import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { ReasonModal } from '@/shared/ui/ReasonModal';

/**
 * `minLength` у діалозі причини (директива №15, §2, Шар 2: `ReasonDialog` —
 * «`minLength`, кнопка вимкнена до валідності»).
 *
 * ⛔ Доводиться саме МЕЖА, обидва її боки. Тест лише на «дев'ять символів —
 * вимкнено» лишився б зеленим у компоненті, який не вмикає кнопку ніколи;
 * тест лише на «десять — увімкнено» — у тому, який не вимикає її взагалі
 * (тобто в чинному, до цієї зміни).
 */
/**
 * ⚠ `minLength` передається НАПРЯМУ, а не розкладкою
 * `{...(x === undefined ? {} : { minLength: x })}` — хоч саме так у цьому
 * репозиторії й пишуть необов'язкові пропи під `exactOptionalPropertyTypes`
 * (`AsyncBoundary.test.tsx`). Причина названа заміром: TypeScript НЕ
 * перевіряє зайвих полів у розкладці, тож із тим записом прибраний із
 * компонента проп не дав ЖОДНОЇ помилки `tsc` — «пропа немає» читалося б як
 * «проп правильний». Тут проп оголошений як `number | undefined`, тому
 * прямий запис законний і водночас ловить його зникнення.
 */
function show(props: { minLength?: number; onConfirm?: (reason: string) => void }): void {
  render(
    <MantineProvider>
      <ReasonModal
        opened
        title="Відхилити аркуш"
        label="Причина"
        confirmLabel="Відхилити"
        onConfirm={props.onConfirm ?? vi.fn()}
        onClose={vi.fn()}
        minLength={props.minLength}
      />
    </MantineProvider>,
  );
}

function type(value: string): void {
  fireEvent.change(screen.getByRole('textbox', { name: /Причина/ }), { target: { value } });
}

function confirm(): HTMLButtonElement {
  return screen.getByRole('button', { name: 'Відхилити' }) as HTMLButtonElement;
}

afterEach(() => {
  cleanup();
});

describe('ReasonModal: minLength — межа рівно на minLength', () => {
  it('на один символ менше за поріг кнопка ВИМКНЕНА', () => {
    show({ minLength: 10 });
    type('x'.repeat(9));

    expect(confirm().disabled).toBe(true);
  });

  it('рівно minLength — кнопка УВІМКНЕНА', () => {
    show({ minLength: 10 });
    type('x'.repeat(10));

    expect(confirm().disabled).toBe(false);
  });

  it('понад поріг — лишається увімкненою', () => {
    show({ minLength: 10 });
    type('x'.repeat(40));

    expect(confirm().disabled).toBe(false);
  });

  /**
   * ⛔ Пробіли порогу НЕ вдовольняють, і це не дрібниця: на сервер іде
   * `reason.trim()` (`onConfirm` у компоненті), тож інакше десять натискань
   * на пробіл вмикали б кнопку, домен отримував би порожній рядок і відмовляв
   * `ECR-DOC-0422` — рівно те, чого діалог мав не допустити.
   */
  it('десять пробілів порогу не вдовольняють — рахуються символи після trim', () => {
    show({ minLength: 10 });
    type(' '.repeat(12));

    expect(confirm().disabled).toBe(true);
  });

  it('значуща причина з пробілами по краях проходить за своєю довжиною', () => {
    const onConfirm = vi.fn();
    show({ minLength: 10, onConfirm });
    type('  неповні дані  ');

    expect(confirm().disabled).toBe(false);

    confirm().click();
    expect(onConfirm).toHaveBeenCalledWith('неповні дані');
  });
});

/**
 * ⛔ Друга половина доказу: без `minLength` поведінка лишилася ДОСЛІВНО
 * чинною — поріг один символ, а не нуль. Нуль означав би увімкнену кнопку над
 * порожнім полем, тобто гарантований `422`, заради якого діалог і існує.
 *
 * ⚠ Це й мутаційна проба на «прибрали зовсім»: якщо `minLength` зникне з
 * компонента, тести вище перестануть компілюватися, а цей лишиться зеленим —
 * тобто «пропа немає» ніколи не прочитається як «проп правильний».
 */
describe('ReasonModal: без minLength поріг — один символ', () => {
  it('порожнє поле — кнопка вимкнена', () => {
    show({});

    expect(confirm().disabled).toBe(true);
  });

  it('один значущий символ — кнопка увімкнена', () => {
    show({});
    type('!');

    expect(confirm().disabled).toBe(false);
  });

  it('самі пробіли — кнопка вимкнена', () => {
    show({});
    type('   ');

    expect(confirm().disabled).toBe(true);
  });
});
