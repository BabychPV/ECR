import type { JSX } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Group,
  NativeSelect,
  NumberInput,
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
import {
  EditableFieldDataTypes,
  NumericFieldTypes,
  RuleKinds,
  Severities,
  isComplete,
  isFieldComplete,
  type FieldDataType,
  type FieldDraft,
  type RuleDraft,
} from './definition';
import { localized } from '@/shared/i18n/localized';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

/**
 * Поля довідника (`ФВ-8.3`, `ФВ-8.12`).
 *
 * ⛔ НАЯВНІ поля показуються, а не правляться, і це рішення (`D2-202`). Код,
 * тип і ключовість наявного поля перетлумачують уже збережені значення: код —
 * те, чим на поле посилаються вирази і мапінг; тип — те, як читається колонка
 * `dic.RegistryValue`; ключовість входить у бізнес-ключ запису. Форма, яка
 * дає їх змінити, обіцяє те, чого сервер не робить (`SaveRegistryDefinitionHandler.ApplyFields`
 * відхиляє розбіжність коду й типу для наявного `id` окремим повідомленням).
 *
 * ⚠ ДОДАВАННЯ нового поля — інша дія, і D2-202 її не забороняє: сервер уже
 * розрізняє «нове» від «правки наявного» за `id === null`
 * (`RegistryFieldSaveDto`), рівно як і для правил нижче. До цього фіксу
 * інтерфейс просто не мав кнопки, що ним скористалася б — довідник, заведений
 * через `/admin/registries`, не міг отримати жодного поля, а без ключового
 * поля сервер відмовляє зберегти будь-яку зміну опису взагалі
 * (`ECR-REG-0422`, «залишився б без жодного ключового поля»).
 *
 * ⚠ Ознака «ключове» показана явно: саме за ключовими полями звужується
 * доступ (`RoleAssignment.ScopeJson`), і адміністратор, який не бачить, які
 * поля ключові, не може пояснити, чому область прав виглядає саме так.
 */
export function RegistryFields({
  definition,
  canEdit,
  newFields,
  registryOptions,
  onAddField,
  onChangeField,
  onRemoveField,
}: {
  readonly definition: RegistryDefinitionDto;
  readonly canEdit: boolean;

  /** Нові поля, додані в цьому сеансі — ще не збережені (`FieldDraft`). */
  readonly newFields: readonly FieldDraft[];

  /** Довідники для вибору цілі поля типу `Lookup` — код і назва. */
  readonly registryOptions: readonly { value: string; label: string }[];
  readonly onAddField: () => void;
  readonly onChangeField: (index: number, field: FieldDraft) => void;
  readonly onRemoveField: (index: number) => void;
}): JSX.Element {
  return (
    <Stack gap="xs">
      <Group justify="space-between" align="center">
        <Title order={2} size="h5">
          {t('registries.tabFields')}
        </Title>

        {/* ⛔ Це і є фікс: до нього на всій сторінці конструктора не було
            жодного контролю, що додавав поле, — на відміну від сусідньої
            вкладки `RegistryRules`, у якої «Додати правило» працює навіть
            при нулі правил. */}
        {canEdit && (
          <Button size="xs" variant="default" onClick={onAddField}>
            {t('registries.addField')}
          </Button>
        )}
      </Group>

      <Table striped highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('registries.code')}</Table.Th>
            <Table.Th>{t('registries.name')}</Table.Th>
            <Table.Th>{t('registries.dataType')}</Table.Th>
            <Table.Th>{t('registries.required')}</Table.Th>
            <Table.Th>{t('registries.keyField')}</Table.Th>
            <Table.Th>{t('registries.lookup')}</Table.Th>
            <Table.Th>{t('registries.unit')}</Table.Th>
            {newFields.length > 0 && <Table.Th />}
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
              <Table.Td>{field.unitId ?? '—'}</Table.Td>
              {newFields.length > 0 && <Table.Td />}
            </Table.Tr>
          ))}

          {/* ⚠ Лише НОВІ поля — рядок редагований. Наявне поле в цій таблиці
              вище лишається текстом, не інпутом: D2-202 стосується саме їх. */}
          {newFields.map((draft, index) => {
            const isLookup = draft.dataType === 'Lookup';
            const isNumeric = NumericFieldTypes.includes(draft.dataType);

            return (
              <Table.Tr key={`new-${index}`}>
                <Table.Td>
                  <TextInput
                    size="xs"
                    label={t('registries.code')}
                    disabled={!canEdit}
                    value={draft.code}
                    onChange={(event) =>
                      onChangeField(index, { ...draft, code: event.currentTarget.value })
                    }
                  />
                </Table.Td>
                <Table.Td>
                  <TextInput
                    size="xs"
                    label={t('registries.name')}
                    disabled={!canEdit}
                    value={draft.name}
                    onChange={(event) =>
                      onChangeField(index, { ...draft, name: event.currentTarget.value })
                    }
                  />
                </Table.Td>
                <Table.Td>
                  <NativeSelect
                    size="xs"
                    label={t('registries.dataType')}
                    disabled={!canEdit}
                    value={draft.dataType}
                    data={[...EditableFieldDataTypes]}
                    onChange={(event) => {
                      const dataType = event.currentTarget.value as FieldDataType;
                      onChangeField(index, {
                        ...draft,
                        dataType,
                        lookupRegistryDefId: dataType === 'Lookup' ? draft.lookupRegistryDefId : null,
                        unitId: NumericFieldTypes.includes(dataType) ? draft.unitId : null,
                      });
                    }}
                  />
                </Table.Td>

                {/* ⛔ Не інпут: нове поле обов'язковим бути не може
                    (`FieldDraft` навіть не носить цього прапорця) — форма не
                    показує вибору там, де сервер однаково відмовить. */}
                <Table.Td>—</Table.Td>

                <Table.Td>
                  <Checkbox
                    aria-label={t('registries.keyField')}
                    disabled={!canEdit}
                    checked={draft.isKey}
                    onChange={(event) =>
                      onChangeField(index, { ...draft, isKey: event.currentTarget.checked })
                    }
                  />
                </Table.Td>

                <Table.Td>
                  {isLookup ? (
                    <NativeSelect
                      size="xs"
                      label={t('registries.lookup')}
                      disabled={!canEdit}
                      value={draft.lookupRegistryDefId === null ? '' : String(draft.lookupRegistryDefId)}
                      data={[{ value: '', label: '—' }, ...registryOptions]}
                      onChange={(event) =>
                        onChangeField(index, {
                          ...draft,
                          lookupRegistryDefId:
                            event.currentTarget.value === '' ? null : Number(event.currentTarget.value),
                        })
                      }
                    />
                  ) : (
                    '—'
                  )}
                </Table.Td>

                <Table.Td>
                  {isNumeric ? (
                    <NumberInput
                      size="xs"
                      label={t('registries.unit')}
                      disabled={!canEdit}
                      value={draft.unitId ?? ''}
                      onChange={(value) =>
                        onChangeField(index, { ...draft, unitId: typeof value === 'number' ? value : null })
                      }
                    />
                  ) : (
                    '—'
                  )}
                </Table.Td>

                <Table.Td>
                  <Button
                    size="compact-xs"
                    variant="subtle"
                    color="statusError"
                    disabled={!canEdit}
                    onClick={() => onRemoveField(index)}
                  >
                    {t('registries.removeField')}
                  </Button>
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>

      {newFields.some((draft) => !isFieldComplete(draft)) && (
        <Text size="xs" c="dimmed">
          {t('registries.fieldIncomplete')}
        </Text>
      )}
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
 *
 * ⚠ Автор показується ІМ'ЯМ, а не голим `changedByUserId` (сиблінг-виправлення
 * до того, що вже пропущено в `AuditPage.tsx`): число саме по собі не
 * відповідає на «хто це зробив» нікому, крім того, хто тримає в голові
 * таблицю користувачів. Мапу `userId → displayName` будує сторінка
 * (`RegistryConstructorPage.tsx`, окремий запит `/api/v1/users`) — цей
 * компонент лишається чистим і не знає, звідки мапа взялася.
 *
 * ⛔ Порожня мапа НЕ падає і не ховає рядок: право читати перелік
 * користувачів (`Security.ManageUsers`) — ІНШЕ право за `Registry.View`, і
 * глядач без нього однаково має побачити ЦЕЙ запис історії — просто без
 * імені автора, з голим ідентифікатором і бейджем «нерозв'язано». Те саме —
 * коли користувача видалили і його немає в першій сторінці переліку.
 */
export function RegistryHistory({
  entries,
  userNames,
  usersResolved,
}: {
  readonly entries: readonly RegistryHistoryEntryDto[];

  /** Мапа `userId → displayName` — будує сторінка з відповіді `/api/v1/users`. */
  readonly userNames: ReadonlyMap<number, string>;

  /**
   * Чи запит переліку користувачів уже ЗАВЕРШИВСЯ (успіхом або відмовою, у
   * т.ч. `403`).
   *
   * ⚠ Доки він ще триває, бейдж «нерозв'язано» НЕ показуємо: мапа порожня і
   * під час завантаження теж, і без цього прапорця бейдж спалахнув би на мить
   * навіть у того, хто МАЄ право, — і одразу зник, щойно відповідь прийде.
   */
  readonly usersResolved: boolean;
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
              {/* ⚠ БЕЗ `dateOnly`: `changedAt` — МОМЕНТ зміни в UTC
                  (`RegistryHistoryEntryDto`, `Format: date-time`), і година
                  тут не декорація. Питання, на яке дивляться в цій колонці, —
                  «яка з двох правок опису була пізніша»; календарний день на
                  нього не відповідає, бо правки опису довідника лягають
                  пачками в один день.

                  ⚠ `precise` теж НЕ поставлено: секунда тут нічого не
                  вирішує — ключ рядка й так `changedAt:operation`, а дві
                  правки в одну хвилину відрізняються операцією і причиною, а
                  не секундою. Зайва точність на екрані читається як
                  важливість (див. `TimestampProps.precise`). Точне значення
                  нікуди не діло́ся — воно в `dateTime`. */}
              <Table.Td>
                <Timestamp value={entry.changedAt} />
              </Table.Td>
              <Table.Td>{entry.operation}</Table.Td>
              <Table.Td>{entry.changeReason ?? '—'}</Table.Td>
              <Table.Td>
                <HistoryAuthor
                  userId={entry.changedByUserId}
                  userNames={userNames}
                  usersResolved={usersResolved}
                />
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

/**
 * Одна клітинка автора запису історії — ім'я, або голий ідентифікатор і
 * бейдж, коли ім'я нерозв'язне (`RegistryHistory` вище).
 */
function HistoryAuthor({
  userId,
  userNames,
  usersResolved,
}: {
  readonly userId: number;
  readonly userNames: ReadonlyMap<number, string>;
  readonly usersResolved: boolean;
}): JSX.Element {
  const name = userNames.get(userId);
  if (name !== undefined) return <>{name}</>;

  // Запит переліку користувачів ще триває: показати голий ідентифікатор БЕЗ
  // бейджа — інакше «нерозв'язано» спалахнуло б на мить у кожного, у кого
  // право є.
  if (!usersResolved) return <>{userId}</>;

  return (
    <Group gap="xs" wrap="nowrap">
      <Text span>{userId}</Text>
      <Badge size="xs" variant="light" color="gray">
        {t('registries.userUnresolved')}
      </Badge>
    </Group>
  );
}



