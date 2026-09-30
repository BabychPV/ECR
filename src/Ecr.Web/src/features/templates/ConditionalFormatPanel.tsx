import { useEffect, useRef, useState, type JSX } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  ColorInput,
  Divider,
  Group,
  Loader,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
} from '@mantine/core';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  ConditionOperators,
  emptyRule,
  firstMatchingRule,
  operandCount,
  ruleFromWire,
  ruleToWire,
  whyRuleIncomplete,
  type ConditionOperator,
  type ConditionalRule,
} from './conditionalFormat';
import {
  getConditionalFormats,
  isStaleConditionalFormats,
  saveConditionalFormats,
  type ConditionalFormatRuleDto,
  type ConditionalFormatSet,
} from './conditionalFormatApi';

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

/** Ключ запиту правил версії: редактор кожної таблиці читає той самий набір. */
const rulesKey = (templateVersionId: number) => ['conditionalFormats', templateVersionId] as const;

/**
 * Точка відліку чернетки: правила ЦІЄЇ таблиці, правила решти колонок версії
 * (`PUT` замінює набір ВЕРСІЇ цілком — їх треба відіслати назад незмінними) і
 * версія набору, з якої почалася правка (`If-Match`).
 */
interface Seed {
  readonly own: readonly ConditionalRule[];
  readonly others: readonly ConditionalFormatRuleDto[];
  readonly etag: string | null;
}

function seedOf(set: ConditionalFormatSet, codes: ReadonlySet<string>): Seed {
  const own: ConditionalRule[] = [];
  const others: ConditionalFormatRuleDto[] = [];

  for (const wire of set.rules) {
    const rule = codes.has(wire.columnCode) ? ruleFromWire(wire) : null;
    // ⚠ Правило з невідомим оператором не губиться: воно їде назад як було.
    if (rule === null) others.push(wire);
    else own.push(rule);
  }

  return { own, others, etag: set.etag };
}

/** Канонічний вигляд набору таблиці: те, що піде на сервер. */
function canonical(rules: readonly ConditionalRule[]): string {
  return JSON.stringify(rules.map(ruleToWire));
}

/**
 * Умовне форматування таблиці шаблону (`ФВ-2.7`).
 *
 * Правила читаються й зберігаються на сервері (`conditionalFormatApi.ts`), а
 * перевіряються на значенні-прикладі тим самим `firstMatchingRule`, яким їх
 * застосовує сітка документа.
 *
 * ⛔ `PUT` замінює набір ВЕРСІЇ, а редактор відкрито для ОДНІЄЇ таблиці: правила
 * інших колонок відсилаються назад такими, якими їх прочитали (`Seed.others`).
 *
 * ⛔ Версія набору (`If-Match`) — та, з якої почалася ЦЯ чернетка. Відмова
 * `409` (хтось зберіг правила після відкриття): набір перечитується, чернетка
 * лишається, над кнопками — пояснення і «Відкинути мої зміни»; наступне
 * «Зберегти» — усвідомлена заміна чужих правил. Той самий взірець, що
 * `GrantsPanel.tsx`.
 *
 * ⚠ Лінивий чанк: сторінка версії вантажить його лише при відкритті діалогу.
 */
export function ConditionalFormatPanel({
  templateVersionId,
  columns,
  canEdit,
}: {
  readonly templateVersionId: number;
  /** Колонки таблиці: код і підпис. */
  readonly columns: readonly { readonly code: string; readonly label: string }[];
  /** Чернетка версії і право `Template.Edit`; інакше — лише перегляд. */
  readonly canEdit: boolean;
}): JSX.Element {
  const firstColumn = columns[0]?.code ?? '';
  const queryClient = useQueryClient();
  const [seed, setSeed] = useState<Seed | null>(null);
  const [rules, setRules] = useState<readonly ConditionalRule[]>([]);
  const [conflict, setConflict] = useState(false);
  const [sampleColumn, setSampleColumn] = useState(firstColumn);
  const [sample, setSample] = useState('');

  const codes = new Set(columns.map((column) => column.code));
  const dirty = seed !== null && canonical(rules) !== canonical(seed.own);

  // ⚠ Знімок для ефекту: він читає поточну чернетку, а не ту, що була при оголошенні.
  const latest = useRef({ rules, dirty, seed });
  useEffect(() => {
    latest.current = { rules, dirty, seed };
  });

  const loaded = useQuery({
    queryKey: rulesKey(templateVersionId),
    queryFn: () => getConditionalFormats(templateVersionId),
  });

  const columnKey = columns.map((column) => column.code).join('\u001f');

  /*
   * ⛔ Відповідь сервера наповнює чернетку лише тоді, коли в ній нема чого
   * втрачати: ще не наповнена або не змінена. Змінену чернетку фоновий
   * перезапит (фокус вікна, інвалідація) НЕ перезаписує.
   */
  useEffect(() => {
    if (loaded.data === undefined) return;

    const next = seedOf(loaded.data, new Set(columnKey.split('\u001f')));
    if (latest.current.seed !== null && latest.current.dirty) return;

    setSeed(next);
    setRules(next.own);
    setConflict(false);
  }, [loaded.data, columnKey]);

  const save = useMutation({
    mutationFn: (next: { own: readonly ConditionalRule[]; base: Seed }) =>
      saveConditionalFormats(
        templateVersionId,
        [...next.base.others, ...next.own.map(ruleToWire)],
        next.base.etag,
      ),
    onSuccess: (saved) => {
      // ⚠ Збережене стає новою точкою відліку ДО перечитання — інакше чернетка
      // лишилась би «зміненою», доки не приїде фоновий запит.
      const next = seedOf(saved, codes);
      setSeed(next);
      setRules(next.own);
      setConflict(false);
      queryClient.setQueryData(rulesKey(templateVersionId), saved);
      showDone(t('conditionalFormat.saved'));
    },
    onError: async (error) => {
      showApiError(error);
      if (!isStaleConditionalFormats(error)) return;

      // ⚠ Перечитати набір, але НЕ чіпати чернетку: вона — робота людини.
      const fresh = await queryClient.fetchQuery({
        queryKey: rulesKey(templateVersionId),
        queryFn: () => getConditionalFormats(templateVersionId),
        staleTime: 0,
      });
      const next = seedOf(fresh, codes);
      setSeed(next);
      setConflict(canonical(latest.current.rules) !== canonical(next.own));
    },
  });

  if (loaded.isPending) return <Loader size="sm" aria-label={t('common.loading')} />;
  if (loaded.isError) return <ErrorAlert error={loaded.error} />;

  const update = (index: number, next: ConditionalRule): void =>
    setRules((previous) => previous.map((rule, i) => (i === index ? next : rule)));

  const incomplete = rules.some((rule) => whyRuleIncomplete(rule) !== null);
  const readOnly = !canEdit || save.isPending;
  const matched = firstMatchingRule(rules, sampleColumn, sample);
  const columnOptions = columns.map((column) => ({ value: column.code, label: column.label }));
  const operatorOptions = ConditionOperators.map((operator) => ({
    value: operator,
    label: operatorLabel(operator),
  }));

  return (
    <Stack gap="sm">
      {!canEdit && (
        <Alert data-testid="conditional-format-read-only">
          {t('conditionalFormat.readOnly')}
        </Alert>
      )}

      {rules.length === 0 && (
        <Text size="sm" c="dimmed">
          {t('conditionalFormat.none')}
        </Text>
      )}

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
                disabled={readOnly}
                onChange={(value) => update(index, { ...rule, columnCode: value ?? '' })}
              />
              <Select
                label={t('conditionalFormat.operator')}
                data={operatorOptions}
                value={rule.operator}
                allowDeselect={false}
                disabled={readOnly}
                onChange={(value) =>
                  update(index, { ...rule, operator: (value ?? 'gt') as ConditionOperator })
                }
              />
              {operands >= 1 && (
                <TextInput
                  label={t('conditionalFormat.value')}
                  value={rule.value}
                  disabled={readOnly}
                  error={blocker === 'Value' ? t('conditionalFormat.blocker.Value') : undefined}
                  onChange={(event) => update(index, { ...rule, value: event.currentTarget.value })}
                />
              )}
              {operands === 2 && (
                <TextInput
                  label={t('conditionalFormat.valueTo')}
                  value={rule.valueTo}
                  disabled={readOnly}
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
                format="hex"
                value={rule.backgroundHex}
                disabled={readOnly}
                onChange={(value) => update(index, { ...rule, backgroundHex: value })}
              />
              <ColorInput
                label={t('styles.foreground')}
                format="hex"
                value={rule.foregroundHex}
                disabled={readOnly}
                onChange={(value) => update(index, { ...rule, foregroundHex: value })}
              />
              <Switch
                label={t('styles.bold')}
                checked={rule.isBold}
                disabled={readOnly}
                onChange={(event) => update(index, { ...rule, isBold: event.currentTarget.checked })}
              />
              {canEdit && (
                <Button
                  variant="subtle"
                  color="statusError"
                  disabled={save.isPending}
                  onClick={() => setRules((previous) => previous.filter((_, i) => i !== index))}
                >
                  {t('conditionalFormat.remove', { position })}
                </Button>
              )}
            </Group>
            {blocker === 'Color' && (
              <Text size="xs" c="statusError" mt="xs">
                {t('conditionalFormat.blocker.Color')}
              </Text>
            )}
            {blocker === 'Style' && (
              <Text size="xs" c="dimmed" mt="xs">
                {t('conditionalFormat.blocker.Style')}
              </Text>
            )}
          </Box>
        );
      })}

      {canEdit && (
        <Group>
          <Button
            variant="default"
            disabled={save.isPending}
            onClick={() => setRules((previous) => [...previous, emptyRule(firstColumn)])}
          >
            {t('conditionalFormat.add')}
          </Button>
        </Group>
      )}

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

      {conflict && (
        <Alert color="statusWarning" title={t('conditionalFormat.conflictTitle')} data-testid="conditional-format-conflict">
          {t('conditionalFormat.conflict')}
        </Alert>
      )}

      {canEdit && (
        <>
          <Group justify="flex-end">
            {(dirty || conflict) && (
              <Button
                variant="default"
                disabled={save.isPending}
                onClick={() => {
                  if (seed === null) return;
                  setRules(seed.own);
                  setConflict(false);
                }}
              >
                {t('conditionalFormat.discard')}
              </Button>
            )}
            <Button
              loading={save.isPending}
              disabled={seed === null || incomplete || !(dirty || conflict)}
              aria-describedby={incomplete ? 'conditional-format-save-hint' : undefined}
              onClick={() => {
                if (seed !== null) save.mutate({ own: rules, base: seed });
              }}
            >
              {t('conditionalFormat.save')}
            </Button>
          </Group>
          {incomplete && (
            <Text id="conditional-format-save-hint" size="xs" c="dimmed">
              {t('conditionalFormat.incomplete')}
            </Text>
          )}
        </>
      )}
    </Stack>
  );
}
