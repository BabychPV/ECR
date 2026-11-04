import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { t } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { DerivedArguments, argumentKindLabel } from '../DerivedArguments';

/**
 * ✎ 2026-11-02 (похідні аргументи): діалог формули показує список, виведений сервером із виразу, —
 * лише читання, без поля вводу.
 */
describe('DerivedArguments', () => {
  it('показує ім’я, вид, позначку «?» і запис із виразу; поля вводу немає', () => {
    render(
      <MantineProvider theme={testTheme}>
        <DerivedArguments
          items={[
            { kind: 'Argument', name: 'Land_Measure_RunningHours', isOptional: true, text: '@Land_Measure_RunningHours?' },
            { kind: 'Constant', name: 'k1', isOptional: false, text: 'CST.k1' },
            { kind: 'Formula', name: 'Base', isOptional: false, text: '!Base' },
          ]}
        />
      </MantineProvider>,
    );

    const box = screen.getByTestId('derived-arguments');
    // ⛔ Головне: ручного списку більше немає — жодного текстового поля.
    expect(within(box).queryByRole('textbox')).toBeNull();

    const optional = box.querySelector('tr[data-argument="@Land_Measure_RunningHours?"]');
    expect(optional?.textContent).toContain('Land_Measure_RunningHours');
    expect(optional?.textContent).toContain(`? ${t('methodologies.derivedArgumentOptional')}`);
    expect(optional?.textContent).toContain(t('methodologies.argumentKind.input'));

    const constantRow = box.querySelector('tr[data-argument="CST.k1"]');
    expect(constantRow?.textContent).toContain(t('methodologies.argumentKind.constant'));
    expect(constantRow?.textContent).not.toContain(t('methodologies.derivedArgumentOptional'));
  });

  it('порожній список — пояснення, що він з’явиться після збереження', () => {
    render(
      <MantineProvider theme={testTheme}>
        <DerivedArguments items={[]} />
      </MantineProvider>,
    );

    expect(screen.getByText(t('methodologies.derivedArgumentsEmpty'))).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('вид аргументу — літералами ключів', () => {
    expect(argumentKindLabel('Argument')).toBe(t('methodologies.argumentKind.input'));
    expect(argumentKindLabel('Constant')).toBe(t('methodologies.argumentKind.constant'));
    expect(argumentKindLabel('Formula')).toBe(t('methodologies.argumentKind.formula'));
  });
});
