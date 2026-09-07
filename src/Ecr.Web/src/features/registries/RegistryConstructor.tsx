import type { JSX } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Group,
  NativeSelect,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import type {
  RegistryDefinitionDto,
  RegistryHistoryEntryDto,
} from '@/api/types';
import { RuleKinds, Severities, isComplete, type RuleDraft } from './definition';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';

/**
 * Поля довідника (`ФВ-8.3`, `ФВ-8.12`).
 *
 * ⛔ Поля показуються, а не правляться, і це рішення (`D2-202`). Код, тип і
 * ключовість наявного поля перетлумачують уже збережені значення: код — те,
 * чим на поле посилаються вирази і мапінг; тип — те, як читається колонка
 * `dic.RegistryValue`; ключовість входить у бізнес-ключ запису. Форма, яка
 * дає їх змінити, обіцяє те, чого сервер не робить.
 *
 * ⚠ Ознака «ключове» показана явно: саме за ключовими полями звужується
 * доступ (`RoleAssignment.ScopeJson`), і адміністратор, який не бачить, які
 * поля ключові, не може пояснити, чому область прав виглядає саме так.
 */
export function RegistryFields({
  definition,
}: {
  readonly definition: RegistryDefinitionDto;
}): JSX.Element {
  return (
    <Stack gap="xs">
      <Title order={2} size="h5">
        {t('registries.tabFields')}
      </Title>

      <Table striped highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('registries.code')}</Table.Th>
            <Table.Th>{t('registries.name')}</Table.Th>
            <Table.Th>{t('registries.dataType')}</Table.Th>
            <Table.Th>{t('registries.required')}</Table.Th>
            <Table.Th>{t('registries.keyField')}</Table.Th>
            <Table.Th>{t('registries.lookup')}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {definition.fields.map((field) => (
            <Table.Tr key={field.id}>
              <Table.Td>{field.code}</Table.Td>
              <Table.Td>{localized(field.nameL10n)}</Table.Td>
              <Table.Td>{field.dataType}</Table.Td>
              <Table.Td>{field.isRequired ? t('registries.yes') : '—'}</Table.Td>
              <Table.Td>{field.isScopeField ? t('registries.yes') : '—'}</Table.Td>
              <Table.Td>{field.lookupRegistryDefId ?? '—'}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

/**
 * Зв'язки довідника (`ФВ-8.4`).
 *
 * ⚠ Зв'язки **обчислені**, а не оголошені: таблиці `cfg.RegistryRelationDef`
 * у схемі немає (`Q-027`). Тому підпис говорить про те, що є в даних, а не
 * про те, що хтось задумав: `Association` із нулем зв'язків тут не з'явиться
 * взагалі, і це чесніше за порожній рядок «налаштовано, але не заповнено».
 */
export function RegistryRelations({
  definition,
}: {
  readonly definition: RegistryDefinitionDto;
}): JSX.Element {
  if (definition.relations.length === 0) {
    return (
      <Stack gap="xs">
        <Title order={2} size="h5">
          {t('registries.tabRelations')}
        </Title>
        <Text c="dimmed">{t('registries.noRelations')}</Text>
      </Stack>
    );
  }

  return (
    <Stack gap="xs">
      <Title order={2} size="h5">
        {t('registries.tabRelations')}
      </Title>
      <Text size="xs" c="dimmed">
        {t('registries.relationsHint')}
      </Text>

      <Table striped highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('registries.relationKind')}</Table.Th>
            <Table.Th>{t('registries.field')}</Table.Th>
            <Table.Th>{t('registries.target')}</Table.Th>
            <Table.Th>{t('registries.links')}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {definition.relations.map((relation) => (
            <Table.Tr key={`${relation.kind}:${relation.fieldCode ?? relation.linkKind ?? ''}`}>
              <Table.Td>
                <Badge variant="light">{relation.kind}</Badge>
              </Table.Td>
              <Table.Td>{relation.fieldCode ?? '—'}</Table.Td>
              <Table.Td>{relation.targetRegistryCode ?? relation.linkKind ?? '—'}</Table.Td>
              <Table.Td>{relation.linkCount ?? '—'}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

/**
 * Редактор правил довідника — **чотири види** (`H-10`).
 *
 * ⛔ Вид правила обирається лише при створенні: параметри і предикат означають
 * для кожного виду різне, і зміна виду при збережених параметрах дала б
 * правило, синтаксично ціле і таке, що перевіряє не те. Сервер це відхиляє;
 * форма не показує вибору там, де його немає.
 *
 * ⚠ Вимкнене правило лишається у списку. Правило, що колись діяло, — єдине
 * пояснення того, чому наявні записи виглядають саме так, і сховати його
 * означало б лишити адміністратора без відповіді.
 */
export function RegistryRules({
  rules,
  canEdit,
  onChange,
  onAdd,
}: {
  readonly rules: readonly RuleDraft[];
  readonly canEdit: boolean;
  readonly onChange: (index: number, rule: RuleDraft) => void;
  readonly onAdd: () => void;
}): JSX.Element {
  return (
    <Stack gap="xs">
      <Group justify="space-between" align="center">
        <Title order={2} size="h5">
          {t('registries.tabRules')}
        </Title>
        {canEdit && (
          <Button size="xs" variant="default" onClick={onAdd}>
            {t('registries.addRule')}
          </Button>
        )}
      </Group>

      {/* ⛔ Підпис називає число «чотири» прямо: перелік, у якому хтось
          побачить п'ятий вид, і є той дефект, від якого стереже `H-10`. */}
      <Text size="xs" c="dimmed">
        {t('registries.rulesHint')}
      </Text>

      {rules.length === 0 && <Text c="dimmed">{t('registries.noRules')}</Text>}

      {rules.map((rule, index) => (
        <Group key={rule.id ?? `new-${index}`} gap="xs" align="end" wrap="nowrap">
          <TextInput
            size="xs"
            miw={160}
            label={t('registries.ruleCode')}
            value={rule.code}
            disabled={!canEdit || rule.id !== null}
            onChange={(event) => onChange(index, { ...rule, code: event.currentTarget.value })}
          />

          {rule.id === null ? (
            <NativeSelect
              size="xs"
              miw={150}
              label={t('registries.ruleKind')}
              value={rule.ruleKind}
              data={[...RuleKinds]}
              onChange={(event) =>
                onChange(index, { ...rule, ruleKind: event.currentTarget.value })
              }
            />
          ) : (
            <Badge variant="light" mb="xs">
              {rule.ruleKind}
            </Badge>
          )}

          <TextInput
            size="xs"
            flex={1}
            label={t('registries.expression')}
            value={rule.expression}
            disabled={!canEdit}
            error={canEdit && !isComplete(rule) ? t('registries.ruleIncomplete') : undefined}
            onChange={(event) =>
              onChange(index, { ...rule, expression: event.currentTarget.value })
            }
          />

          <NativeSelect
            size="xs"
            miw={120}
            label={t('registries.severity')}
            value={rule.severity}
            data={[...Severities]}
            disabled={!canEdit}
            onChange={(event) => onChange(index, { ...rule, severity: event.currentTarget.value })}
          />

          <Checkbox
            mb="xs"
            label={t('registries.ruleActive')}
            checked={rule.isActive}
            disabled={!canEdit}
            onChange={(event) =>
              onChange(index, { ...rule, isActive: event.currentTarget.checked })
            }
          />
        </Group>
      ))}
    </Stack>
  );
}

/**
 * Мапінг: звідки береться значення поля (`ФВ-8.11`).
 *
 * ⛔ Тільки читання. Мапінг заводять на екрані джерела, де поруч є перелік
 * тегів; тут він потрібен, щоб відповісти на питання «звідки береться це
 * поле» — без нього поле, яке наповнює зовнішня система, виглядає як звичайне,
 * а правити його марно.
 *
 * ⚠ Показані ОБИДВІ одиниці: розбіжність між одиницею джерела і цільовою —
 * «найчастіше джерело мовчазних розбіжностей у числах» (`ФВ-16.9`).
 */
export function RegistryMappings({
  definition,
}: {
  readonly definition: RegistryDefinitionDto;
}): JSX.Element {
  if (definition.mappings.length === 0) {
    return (
      <Stack gap="xs">
        <Title order={2} size="h5">
          {t('registries.tabMapping')}
        </Title>
        <Text c="dimmed">{t('registries.noMappings')}</Text>
      </Stack>
    );
  }

  return (
    <Stack gap="xs">
      <Title order={2} size="h5">
        {t('registries.tabMapping')}
      </Title>

      <Table striped highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('registries.field')}</Table.Th>
            <Table.Th>{t('registries.source')}</Table.Th>
            <Table.Th>{t('registries.sourceField')}</Table.Th>
            <Table.Th>{t('registries.transform')}</Table.Th>
            <Table.Th>{t('registries.units')}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {definition.mappings.map((mapping) => (
            <Table.Tr key={mapping.fieldMapId}>
              <Table.Td>{mapping.fieldCode}</Table.Td>
              <Table.Td>{mapping.sourceCode}</Table.Td>
              <Table.Td>{mapping.sourceField}</Table.Td>
              <Table.Td>{mapping.transformCode ?? '—'}</Table.Td>
              <Table.Td>
                {(mapping.sourceUnitCode ?? '—') + ' → ' + (mapping.targetUnitCode ?? '—')}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

/**
 * Історія опису довідника (`ФВ-8.12`).
 *
 * ⛔ Показується ПРИЧИНА, а не діагностичний diff. Питання, на яке цей екран
 * відповідає, — «чому тут з'явилося це поле», і відповідь на нього пише автор
 * зміни, а не система. Знімки `oldJson`/`newJson` лишаються в журналі для
 * розслідування, але на екрані вони витіснили б єдине, що читається.
 */
export function RegistryHistory({
  entries,
}: {
  readonly entries: readonly RegistryHistoryEntryDto[];
}): JSX.Element {
  if (entries.length === 0) {
    return (
      <Stack gap="xs">
        <Title order={2} size="h5">
          {t('registries.tabHistory')}
        </Title>
        <Text c="dimmed">{t('registries.noHistory')}</Text>
      </Stack>
    );
  }

  return (
    <Stack gap="xs">
      <Title order={2} size="h5">
        {t('registries.tabHistory')}
      </Title>

      <Table striped highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('registries.changedAt')}</Table.Th>
            <Table.Th>{t('registries.operation')}</Table.Th>
            <Table.Th>{t('registries.reason')}</Table.Th>
            <Table.Th>{t('registries.author')}</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {entries.map((entry) => (
            <Table.Tr key={`${entry.changedAt}:${entry.operation}`}>
              <Table.Td>{entry.changedAt}</Table.Td>
              <Table.Td>{entry.operation}</Table.Td>
              <Table.Td>{entry.changeReason ?? '—'}</Table.Td>
              <Table.Td>{entry.changedByUserId}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}



