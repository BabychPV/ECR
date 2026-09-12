import type { JSX } from 'react';
import { Alert, Button, Group, Stack, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { ExpressionEditor } from '@/features/expressions/ExpressionEditor';
import type { ExpressionPlacement } from '@/features/expressions/api';
import { type FormulaBlocker, type FormulaDraft, whyCannotSaveFormula } from './formula';

/**
 * Форма формули колонки чи рядка — наступний вертикальний зріз авторства
 * структури шаблону (`W5.3`), за зразком `SheetEditor.tsx` (W5.0).
 *
 * ⛔ Редактор виразу — не нове поле вводу, а вже наявний `ExpressionEditor`
 * (`ФВ-9.15a`, `D-113`): той самий компонент, яким уже користуються формули
 * методології (`MethodologyVersionsPage`). Монако, підказки з довідника
 * функцій і перевірка `POST /expressions/validate` під час введення тут
 * ідентичні — і мали б бути ідентичними: правило «як виглядає правильна
 * формула» одне на всю систему, а не окреме для кожного місця, де формулу
 * можна написати.
 *
 * ⚠ Діалект НЕ вибирається тут — на відміну від методології
 * (`MethodologyVersionsPage`, де діалект теж фіксований, лише інший).
 * Формула КОЛОНКИ ЧИ РЯДКА таблиці посилається на комірки (`[Jan]`), а
 * діалект `Methodology` посилань на комірки не дозволяє взагалі (`02b` §3.4,
 * `allowsCellRefs`) — вибір тут завжди один, і перемикач лише дав би людині
 * обрати значення, яке однаково не пройде перевірку.
 */
export function FormulaEditor({
  draft,
  templateVersionId,
  disabled,
  saving,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: FormulaDraft;
  templateVersionId: number;
  disabled: boolean;
  saving: boolean;
  onChange: (next: FormulaDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSaveFormula(draft);

  const placement: ExpressionPlacement = {
    templateVersionId,
    tableDefId: draft.tableDefId,
    ...(draft.scope === 'Column'
      ? { columnDefId: Number(draft.target) }
      : { rowKey: draft.target }),
  };

  return (
    <Stack gap="sm">
      <Text size="sm" c="dimmed">
        {draft.scope === 'Column' ? t('formulas.targetColumn') : t('formulas.targetRow')}
      </Text>

      <ExpressionEditor
        value={draft.expression}
        onChange={(expression) => onChange({ ...draft, expression })}
        dialect="Template"
        placement={placement}
        ariaLabel={t('formulas.expression')}
        height="140px"
      />

      {blocker !== null && <Alert color="statusWarning">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('formulas.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: FormulaBlocker): string {
  switch (blocker) {
    case 'Expression':
      return t('formulas.errExpression');
    default:
      return blocker;
  }
}
