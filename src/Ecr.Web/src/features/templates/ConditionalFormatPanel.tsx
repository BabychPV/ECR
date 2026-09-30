import { useState, type JSX } from 'react';
import {
  Alert,
  Box,
  Button,
  ColorInput,
  Divider,
  Group,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
} from '@mantine/core';
import { t } from '@/shared/i18n';
import {
  ConditionOperators,
  emptyRule,
  firstMatchingRule,
  operandCount,
  whyRuleIncomplete,
  type ConditionOperator,
  type ConditionalRule,
} from './conditionalFormat';

/**
 * Підпис оператора. ⚠ Кожен ключ — літералом, без шаблонного рядка: сторож
 * `EndpointCoverageTests` перевіряє, що кожен ключ каталогу видно в коді.
 */
function operatorLabel(operator: ConditionOperator): string {
  switch (operator) {
    case 'gt':
      return t('conditionalFormat.op.gt');
    case 'ge':
      return t('conditionalFormat.op.ge');
    case 'lt':
      return t('conditionalFormat.op.lt');
    case 'le':
      return t('conditionalFormat.op.le');
    case 'eq':
      return t('conditionalFormat.op.eq');
    case 'ne':
      return t('conditionalFormat.op.ne');
    case 'between':
      return t('conditionalFormat.op.between');
    case 'empty':
      return t('conditionalFormat.op.empty');
    case 'notEmpty':
      return t('conditionalFormat.op.notEmpty');
  }
}

/**
 * Умовне форматування таблиці шаблону (`ФВ-2.7`).
 *
 * ⛔ ЗБЕРЕЖЕННЯ ВИМКНЕНЕ — сервер правил не зберігає (див. `conditionalFormat.ts`).
 * Редактор при цьому НЕ бутафорія: правила складаються й одразу
 * перевіряються на значенні-прикладі тим самим `firstMatchingRule`, яким їх
 * застосовуватиме сітка. Що саме бракує, щоб зберегти, — сказано людині
 * прямо (`conditionalFormat.unavailable`), а не прихованою кнопкою.
 *
 * ⚠ Лінивий чанк: сторінка версії вантажить його лише при відкритті діалогу.
 */
export function ConditionalFormatPanel({
  columns,
}: {
  /** Колонки таблиці: код і підпис. */
  readonly columns: readonly { readonly code: string; readonly label: string }[];
}): JSX.Element {
  const firstColumn = columns[0]?.code ?? '';
  const [rules, setRules] = useState<readonly ConditionalRule[]>(() => [emptyRule(firstColumn)]);
  const [sampleColumn, setSampleColumn] = useState(firstColumn);
  const [sample, setSample] = useState('');

  const update = (index: number, next: ConditionalRule): void =>
    setRules((previous) => previous.map((rule, i) => (i === index ? next : rule)));

  const matched = firstMatchingRule(rules, sampleColumn, sample);
  const columnOptions = columns.map((column) => ({ value: column.code, label: column.label }));
  const operatorOptions = ConditionOperators.map((operator) => ({
    value: operator,
    label: operatorLabel(operator),
  }));

  return (
    <Stack gap="sm">
      <Alert color="statusWarning" title={t('conditionalFormat.unavailableTitle')}>
        {t('conditionalFormat.unavailable')}
      </Alert>

      {rules.map((rule, index) => {
        const blocker = whyRuleIncomplete(rule);
        const operands = operandCount(rule.operator);
        const position = index + 1;

        return (
          <Box key={index} role="group" aria-label={t('conditionalFormat.rule', { position })}>
            <Group grow align="flex-start">
              <Select
                label={t('conditionalFormat.column')}
                data={columnOptions}
                value={rule.columnCode === '' ? null : rule.columnCode}
                allowDeselect={false}
                onChange={(value) => update(index, { ...rule, columnCode: value ?? '' })}
              />
              <Select
                label={t('conditionalFormat.operator')}
                data={operatorOptions}
                value={rule.operator}
                allowDeselect={false}
                onChange={(value) =>
                  update(index, { ...rule, operator: (value ?? 'gt') as ConditionOperator })
                }
              />
              {operands >= 1 && (
                <TextInput
                  label={t('conditionalFormat.value')}
                  value={rule.value}
                  error={blocker === 'Value' ? t('conditionalFormat.blocker.Value') : undefined}
                  onChange={(event) => update(index, { ...rule, value: event.currentTarget.value })}
                />
              )}
              {operands === 2 && (
                <TextInput
                  label={t('conditionalFormat.valueTo')}
                  value={rule.valueTo}
                  error={
                    blocker === 'ValueTo' || blocker === 'Range'
                      ? t(blocker === 'Range' ? 'conditionalFormat.blocker.Range' : 'conditionalFormat.blocker.ValueTo')
                      : undefined
                  }
                  onChange={(event) => update(index, { ...rule, valueTo: event.currentTarget.value })}
                />
              )}
            </Group>
            <Group grow align="flex-end" mt="xs">
              <ColorInput
                label={t('styles.background')}
                value={rule.backgroundHex}
                onChange={(value) => update(index, { ...rule, backgroundHex: value })}
              />
              <ColorInput
                label={t('styles.foreground')}
                value={rule.foregroundHex}
                onChange={(value) => update(index, { ...rule, foregroundHex: value })}
              />
              <Switch
                label={t('styles.bold')}
                checked={rule.isBold}
                onChange={(event) => update(index, { ...rule, isBold: event.currentTarget.checked })}
              />
              <Button
                variant="subtle"
                color="statusError"
                onClick={() => setRules((previous) => previous.filter((_, i) => i !== index))}
              >
                {t('conditionalFormat.remove', { position })}
              </Button>
            </Group>
            {blocker === 'Style' && (
              <Text size="xs" c="dimmed" mt="xs">
                {t('conditionalFormat.blocker.Style')}
              </Text>
            )}
          </Box>
        );
      })}

      <Group>
        <Button variant="default" onClick={() => setRules((previous) => [...previous, emptyRule(firstColumn)])}>
          {t('conditionalFormat.add')}
        </Button>
      </Group>

      <Divider label={t('conditionalFormat.preview')} />

      <Group grow align="flex-end">
        <Select
          label={t('conditionalFormat.column')}
          data={columnOptions}
          value={sampleColumn === '' ? null : sampleColumn}
          allowDeselect={false}
          onChange={(value) => setSampleColumn(value ?? '')}
        />
        <TextInput
          label={t('conditionalFormat.sample')}
          value={sample}
          onChange={(event) => setSample(event.currentTarget.value)}
        />
      </Group>
      <Box
        data-conditional-preview={matched === null ? 'none' : 'match'}
        p="xs"
        bd="1px solid var(--mantine-color-default-border)"
        {...(matched !== null && matched.backgroundHex !== '' ? { bg: matched.backgroundHex } : {})}
        {...(matched !== null && matched.foregroundHex !== '' ? { c: matched.foregroundHex } : {})}
        {...(matched?.isBold === true ? { fw: 700 } : {})}
      >
        {sample.length === 0 ? t('conditionalFormat.sampleEmpty') : sample}
      </Box>
      <Text size="sm" aria-live="polite">
        {matched === null
          ? t('conditionalFormat.noMatch')
          : t('conditionalFormat.matched', { position: rules.indexOf(matched) + 1 })}
      </Text>

      <Group justify="flex-end">
        <Button disabled aria-describedby="conditional-format-save-hint">
          {t('conditionalFormat.save')}
        </Button>
      </Group>
      <Text id="conditional-format-save-hint" size="xs" c="dimmed">
        {t('conditionalFormat.saveUnavailable')}
      </Text>
    </Stack>
  );
}
