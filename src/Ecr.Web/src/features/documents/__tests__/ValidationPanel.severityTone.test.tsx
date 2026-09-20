import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { ValidationPanel } from '@/features/documents/ValidationPanel';
import { renderWithMantine } from '@/test/render';
import { statusTone } from '@/shared/ui/StatusBadge';

/**
 * Рівень зауваження малювала власна `colorOf(severity)` — шістнадцята за
 * ліком локальна таблиця кольору статусу в застосунку.
 *
 * ⛔ Дефект у ній був той самий, що й у знятої `JobsPage.stateColor`: гілка
 * «інакше» повертала `'blue'`. Тобто рівень, якого клієнт не знає (а
 * `severity` доходить до нього простим `string`), виглядав би на екрані
 * точнісінько як `Info` — тихе «усе гаразд» там, де насправді «я цього не
 * розумію».
 *
 * ⚠ Другий бік — текст: `{message.severity}` друкував код сервера
 * англійською на будь-якій мові інтерфейсу.
 *
 * ⛔ Директива №15 §2 вимагає, щоб «іншого способу намалювати статус не
 * лишалося», тож `colorOf` не поправлено, а ВИДАЛЕНО: локальна таблиця на три
 * рядки — це саме той другий спосіб, який розходиться з першим мовчки.
 */

function finding(severity: string, ruleCode: string): {
  readonly tableDefId: number;
  readonly ruleCode: string;
  readonly rowKey: string | null;
  readonly columnCode: string | null;
  readonly severity: string;
  readonly message: string;
} {
  return {
    tableDefId: 1,
    ruleCode,
    rowKey: 'R-1',
    columnCode: 'C-1',
    severity,
    message: `Повідомлення ${ruleCode}`,
  };
}

function tones(): readonly (string | null)[] {
  return [...document.querySelectorAll('[data-status-state]')].map((node) =>
    node.getAttribute('data-status-tone'),
  );
}

describe('ValidationPanel: рівень зауваження — з таблиці набору', () => {
  it('кожен рівень проходить через набір, а не через місцевий колір', () => {
    renderWithMantine(
      <ValidationPanel
        messages={[finding('Error', 'R1'), finding('Warning', 'R2'), finding('Info', 'R3')] as never}
      />,
    );

    const states = [...document.querySelectorAll('[data-status-state]')].map((node) =>
      node.getAttribute('data-status-state'),
    );

    expect(states).toEqual(['Error', 'Warning', 'Info']);

    /*
     * ⛔ Тони звіряються з ТИМ САМИМ джерелом, яким малює решта застосунку.
     * Якби тут стояли літерали ('danger', 'warning', 'neutral'), тест
     * лишився б зеленим і тоді, коли сторінка малює власною таблицею, яка
     * випадково збіглася.
     */
    expect(tones()).toEqual([
      statusTone('severity', 'Error'),
      statusTone('severity', 'Warning'),
      statusTone('severity', 'Info'),
    ]);
  });

  it('НЕВІДОМИЙ рівень помітний, а не схожий на Info', () => {
    /*
     * ⛔ Головне твердження, і саме воно було хибним до цієї правки:
     * `colorOf` віддавала невідомому `'blue'`, тобто новий рівень із сервера
     * виглядав як інформаційний. Набір дає невідомому `warning` — «я цього не
     * знаю» видно.
     */
    renderWithMantine(
      <ValidationPanel messages={[finding('Blocking', 'R9')] as never} />,
    );

    const tone = tones()[0];

    expect(tone).not.toBe(statusTone('severity', 'Info'));
    expect(tone).toBe('warning');
  });

  it('код сервера не потрапляє на екран як видимий текст', () => {
    /*
     * ⚠ Підпис береться з каталогу (`status.severity.*`); без завантаженого
     * каталогу `t()` чесно повертає позначений ключ. Тобто голого слова
     * `Error` у комірці рівня бути не може.
     *
     * ⛔ Мутаційний доказ: поверніть `{message.severity}` у тіло бейджа — і
     * `queryAllByText('Error')` знайде вузол замість нуля.
     */
    renderWithMantine(<ValidationPanel messages={[finding('Error', 'R1')] as never} />);

    expect(screen.queryAllByText('Error')).toHaveLength(0);
    expect(document.body.textContent ?? '').toContain('status.severity.Error');
  });
});
