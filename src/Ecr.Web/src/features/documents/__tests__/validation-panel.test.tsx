import type { JSX } from 'react';
import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ValidationMessageDto } from '@/api/types';
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
const Message = (over: Partial<ValidationMessageDto> = {}): ValidationMessageDto => ({
  severity: 'Error',
  ruleCode: 'CAP',
  message: 'Volume is over the cap',
  rowKey: 'R1',
  columnCode: null,
  ...over,
});

function Panel(props: { messages: readonly ValidationMessageDto[] | null }): JSX.Element {
  return (
    <MantineProvider>
      <ValidationPanel messages={props.messages} />
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
