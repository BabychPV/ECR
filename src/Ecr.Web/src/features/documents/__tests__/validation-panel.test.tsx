import type { JSX } from 'react';
import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ValidationFindingDto } from '@/api/types';
import { ValidationPanel } from '@/features/documents/ValidationPanel';

/**
 * Перелік зауважень перевірки на екрані (директива №09 `W8` п.3, `S-19`).
 *
 * ⛔ Раніше екран показував ЛИШЕ тост із числом: «перевірка знайшла 3
 * помилки». Число без переліку не веде до жодної дії — оператор дізнавався,
 * що щось не так, і не дізнавався ні що саме, ні де. Сервер при цьому віддавав
 * повний список у тій самій відповіді, а клієнт брав із нього `length`.
 *
 * ⚠ Каталог рядків тут не завантажений, тому підписи приходять ключами в
 * `⟦…⟧`. Перевіряються ДАНІ: адреса рядка, код правила, текст.
 */
const Message = (over: Partial<ValidationFindingDto> = {}): ValidationFindingDto => ({
  severity: 'Error',
  ruleCode: 'CAP',
  message: 'Volume is over the cap',
  // ⚠ Таблиця — частина адреси, і в типі вона обов'язкова: `rowKey` унікальний
  // лише всередині своєї таблиці аркуша (`BE-04`).
  tableDefId: 7,
  rowKey: 'R1',
  columnCode: null,
  blocksSave: true,
  ...over,
});

function Panel(props: {
  messages: readonly ValidationFindingDto[] | null;
  onSelect?: (message: ValidationFindingDto) => void;
}): JSX.Element {
  return (
    <MantineProvider>
      <ValidationPanel
        messages={props.messages}
        {...(props.onSelect === undefined ? {} : { onSelect: props.onSelect })}
      />
    </MantineProvider>
  );
}

describe('панель зауважень перевірки', () => {
  it('показує адресу рядка й текст, а не лише кількість', () => {
    render(<Panel messages={[Message()]} />);

    // ⛔ Головне твердження: на екрані є те, за чим можна ПІТИ ВИПРАВИТИ —
    // рядок, правило, текст.
    expect(screen.getByText('R1')).toBeDefined();
    expect(screen.getByText('CAP')).toBeDefined();
    expect(screen.getByText('Volume is over the cap')).toBeDefined();
  });

  it('показує кожне повідомлення, а не перше', () => {
    render(
      <Panel
        messages={[
          Message({ rowKey: 'R1' }),
          Message({ rowKey: 'R2', ruleCode: 'BALANCE', message: 'Balance does not add up' }),
        ]}
      />,
    );

    expect(screen.getByText('R1')).toBeDefined();
    expect(screen.getByText('R2')).toBeDefined();
    expect(screen.getByText('Balance does not add up')).toBeDefined();
  });

  it('зауваження без рядка показує прочерк, а не порожнечу', () => {
    // ⚠ Правило рівня таблиці чи документа адреси рядка не має за
    // визначенням. Порожня клітинка читалася б як «не показали».
    render(<Panel messages={[Message({ rowKey: null, columnCode: null })]} />);

    expect(screen.getAllByText('—').length).toBe(2);
  });

  it('«не перевіряли» і «зауважень немає» — різні стани', () => {
    // ⛔ `null` не малює нічого: зелений напис під документом, якого ніхто не
    // перевіряв, — це неправда про готовність (`A7-04` тим самим коштом).
    render(<Panel messages={null} />);
    expect(screen.queryByText('⟦document.validationCleanHint⟧')).toBeNull();
    expect(screen.queryByText('⟦document.validationTitle⟧')).toBeNull();

    render(<Panel messages={[]} />);
    expect(screen.getByText('⟦document.validationCleanHint⟧')).toBeDefined();
  });
});

describe('перехід від зауваження до комірки (ФВ-5.6)', () => {
  it('клік по тексту зауваження передає його адресу', () => {
    const onSelect = vi.fn();
    const second = Message({ rowKey: 'R2', columnCode: 'C3', message: 'Balance does not add up' });
    render(<Panel messages={[Message(), second]} onSelect={onSelect} />);

    fireEvent.click(screen.getByRole('button', { name: 'Balance does not add up' }));

    // ⛔ Саме те зауваження, по якому клацнули, — з таблицею, рядком і колонкою.
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(second);
  });

  it('перехід — кнопка: доступний з клавіатури і має підказку', () => {
    render(<Panel messages={[Message()]} onSelect={() => undefined} />);

    const button = screen.getByRole('button', { name: 'Volume is over the cap' });
    expect(button.tagName).toBe('BUTTON');
    expect(button.getAttribute('type')).toBe('button');
    expect(button.getAttribute('title')).toBe('⟦document.validationGoTo⟧');
  });

  it('без обробника текст лишається текстом, а не кнопкою в нікуди', () => {
    render(<Panel messages={[Message()]} />);

    expect(screen.queryByRole('button', { name: 'Volume is over the cap' })).toBeNull();
    expect(screen.getByText('Volume is over the cap')).toBeDefined();
  });
});
