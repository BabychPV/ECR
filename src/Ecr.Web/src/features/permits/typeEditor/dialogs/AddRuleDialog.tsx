import { useEffect, useState, type FormEvent, type JSX } from 'react';
import { Button, Group, Modal, SegmentedControl, Select, Stack, Text, Textarea, TextInput } from '@mantine/core';
import type { PermitType, PermitTypeRule } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { allRuleCodes, buildRule, validateRuleInput, type RuleSeverity } from '../checks/ruleOps';
import { pickName } from './names';

/**
 * Діалог «Add a check» (мокап `add-rule`; Permits, PM-08c): що перевіряється, до чого
 * застосовується, вираз, що робити при порушенні.
 *
 * Самодостатній: перелік таблиць бере з `type`, нічого не читає з мережі. Результат — перевірка
 * запиту й код таблиці (`null` — перевірка всього дозволу, тобто шапки типу); додає її до типу
 * хост (`draftModel.addRule`).
 *
 * ⚠ Вираз перевіряє серверний рушій правил довідників під час публікації; клієнт лише вимагає,
 * щоб він не був порожнім (як мокап: «Write the rule expression»).
 */
export interface AddRuleDialogProps {
  readonly opened: boolean;
  readonly type: PermitType;

  /** Таблиця, яка вибрана в «Applies to» спочатку; `null` — весь дозвіл. */
  readonly defaultTableCode?: string | null | undefined;
  readonly onApply: (rule: PermitTypeRule, whereTableCode: string | null) => void;
  readonly onClose: () => void;
}

const WholePermit = '__permit';

export function AddRuleDialog({
  opened,
  type,
  defaultTableCode = null,
  onApply,
  onClose,
}: AddRuleDialogProps): JSX.Element {
  const initialWhere = defaultTableCode ?? WholePermit;
  const [name, setName] = useState('');
  const [expression, setExpression] = useState('');
  const [where, setWhere] = useState(initialWhere);
  const [severity, setSeverity] = useState<RuleSeverity>('Warn');
  const [showErrors, setShowErrors] = useState(false);

  useEffect(() => {
    if (!opened) return;

    setName('');
    setExpression('');
    setWhere(defaultTableCode ?? WholePermit);
    setSeverity('Warn');
    setShowErrors(false);
  }, [opened, defaultTableCode]);

  const problems = validateRuleInput({ name, expression });

  function submit(event: FormEvent): void {
    event.preventDefault();

    if (problems.name || problems.expression) {
      setShowErrors(true);
      document.getElementById(problems.name ? 'pm-rule-name' : 'pm-rule-text')?.focus();

      return;
    }

    onApply(
      buildRule({ name, expression, severity }, allRuleCodes(type)),
      where === WholePermit ? null : where,
    );
    onClose();
  }

  const whereData = [
    { value: WholePermit, label: t('permits.editor.dialogs.addRule.wholePermit') },
    ...type.tables.map((table) => ({
      value: table.code,
      label: t('permits.editor.dialogs.addRule.table', { table: pickName(table.nameL10n) }),
    })),
  ];

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t('permits.editor.dialogs.addRule.title')}
      size="lg"
    >
      <form onSubmit={submit} noValidate>
        <Stack gap="md">
          <TextInput
            id="pm-rule-name"
            label={t('permits.editor.dialogs.addRule.name')}
            placeholder={t('permits.editor.dialogs.addRule.namePlaceholder')}
            value={name}
            data-autofocus
            error={
              showErrors && problems.name ? t('permits.editor.dialogs.addRule.nameRequired') : undefined
            }
            onChange={(event) => {
              setName(event.currentTarget.value);
            }}
          />

          <Select
            id="pm-rule-where"
            label={t('permits.editor.dialogs.addRule.where')}
            data={whereData}
            value={where}
            allowDeselect={false}
            onChange={(next) => {
              if (next !== null) setWhere(next);
            }}
          />

          <Textarea
            id="pm-rule-text"
            label={t('permits.editor.dialogs.addRule.expression')}
            description={t('permits.editor.dialogs.addRule.expressionHint')}
            value={expression}
            minRows={3}
            autosize
            styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
            error={
              showErrors && problems.expression
                ? t('permits.editor.dialogs.addRule.expressionRequired')
                : undefined
            }
            onChange={(event) => {
              setExpression(event.currentTarget.value);
            }}
          />

          {/* ⚠ SegmentedControl, не Radio: стилі Radio відсічені (`mantineCssPrune.ts`). */}
          <Stack gap="xs">
            <Text size="sm" fw={500} id="pm-rule-severity-label">
              {t('permits.editor.dialogs.addRule.severity')}
            </Text>
            <SegmentedControl
              aria-labelledby="pm-rule-severity-label"
              value={severity}
              onChange={(next) => {
                setSeverity(next === 'Block' ? 'Block' : 'Warn');
              }}
              data={[
                { value: 'Warn', label: t('permits.editor.dialogs.addRule.warn') },
                { value: 'Block', label: t('permits.editor.dialogs.addRule.block') },
              ]}
            />
          </Stack>

          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={onClose}>
              {t('common.cancel')}
            </Button>
            <Button type="submit" variant="filled">
              {t('permits.editor.dialogs.addRule.apply')}
            </Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
