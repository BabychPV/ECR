import { useMemo, useState, type JSX } from 'react';
import { Badge, Code, Group, NumberInput, Select, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { toneFills, type StatusTone } from '@/shared/ui/StatusBadge';
import { calculationBindings } from './api';
import {
  defaultRuleCoverageWindow,
  ruleCoverage,
  ruleCoverageCell,
  ruleCoverageOtherRules,
  ruleCoverageWindowEmpty,
  type RuleCoverageCombinationDto,
  type RuleCoverageDto,
  type RuleCoverageState,
} from './ruleCoverage';

/**
 * Вигляд стану комбінації.
 *
 * ⛔ Локальний бейдж, а не `StatusBadge`: стан комбінації — не стан сутності, і
 * в наборі для нього різновиду немає (той самий привід, що в позначки формату
 * зрізу й паузи мапінгу). Кольори — ті самі пари токенів набору (`toneFills`),
 * жодного літерала.
 *
 * ⛔ `Covered` теж МАЄ підпис, хоча позначка «усе гаразд» зазвичай шум. Тут
 * інакше: колонка стану — єдине, що відрізняє покриту комбінацію від
 * конфліктної, і порожня комірка читалася б як «ще не порахували».
 */
const StateTone: Readonly<Record<RuleCoverageState, StatusTone>> = {
  Gap: 'danger',
  Conflict: 'warning',
  Covered: 'muted',
};

/**
 * Підпис стану.
 *
 * ⚠ Ключі підставляються ЛІТЕРАЛАМИ в окремих викликах `t()`, а не таблицею
 * `Looks[state].label`: сторож `Кожен_ключ_який_клієнт_передає_змінною_названий_у_переліку`
 * вимагає для динамічного ключа запису в `DynamicKeySites` (файл гейта), а
 * підпис із трьох варіантів такої ціни не вартий.
 */
function stateLabel(state: RuleCoverageState): string {
  if (state === 'Gap') return t('methodologies.ruleCoverageGap');
  if (state === 'Conflict') return t('methodologies.ruleCoverageConflict');

  return t('methodologies.ruleCoverageCovered');
}

/** Позначка стану комбінації. */
function StateBadge({ state }: { readonly state: RuleCoverageState }): JSX.Element {
  const fill = toneFills[StateTone[state]];

  return (
    <Badge
      size="sm"
      miw="fit-content"
      variant="default"
      bg={fill.bg}
      c={fill.text}
      data-coverage-state={state}
    >
      {stateLabel(state)}
    </Badge>
  );
}

/**
 * Комірки комбінації в порядку `columnDefIds`.
 *
 * ⚠ Одна колонка на всі значення, а не колонка на кожен `columnDefId`: осей
 * стільки, скільки різних ключів згадали правила, і зашити їх у заголовок
 * означало б таблицю змінної ширини — тоді межа «не більше семи колонок»
 * тримається лише доти, доки методологія проста.
 */
function ValueCells({ values }: { readonly values: readonly string[] }): JSX.Element {
  return (
    <Group gap="xs">
      {values.map((raw, index) => {
        const value = ruleCoverageCell(raw);

        return value === null ? (
          <Text key={index} span size="sm" c="dimmed">
            {t('methodologies.ruleCoverageNoCell')}
          </Text>
        ) : (
          <Code key={index}>{value}</Code>
        );
      })}
    </Group>
  );
}

/**
 * Правила комбінації: переможець і решта.
 *
 * ⛔ Переможець виділений НАЧЕРКОМ і власним `data`-атрибутом, а не просто
 * стоїть першим у переліку. Порядок — не ознака: перелік із двох кодів, де
 * перший випадково виявився переможним, відповідає на питання «які правила
 * збіглися», а не на питання «яке з них рахує рядок», а екран існує заради
 * другого.
 *
 * ⚠ Для `Covered` решта — ЗАТІНЕНІ правила нижчого пріоритету, і це не
 * конфлікт; приписка про це стоїть лише там, де затінені справді є.
 */
function RuleCell({
  combination,
}: {
  readonly combination: RuleCoverageCombinationDto;
}): JSX.Element {
  const others = ruleCoverageOtherRules(combination);
  const winner = combination.winnerRuleCode;

  return (
    <Stack gap="xs">
      {winner !== null && (
        <Text span size="sm" fw={600} data-winner-rule={winner}>
          {winner}
        </Text>
      )}

      {others.length > 0 && (
        <Text span size="sm" c="dimmed" data-other-rules={others.join(',')}>
          {others.join(', ')}
          {combination.state === 'Covered' && ` — ${t('methodologies.ruleCoverageShadowed')}`}
        </Text>
      )}
    </Stack>
  );
}

/**
 * Матриця покриття «рядки реальних даних × правила» версії методології
 * (`ФВ-13.4`, `ФВ-13.9`).
 *
 * ⛔ Це НЕ те саме, що `…/coverage` (вихід → колонки). Той екран відповідає на
 * питання «куди лягає порахуване», цей — на питання «що взагалі буде
 * порахованим»: правило, яке не зачіпає жодного рядка, і рядок, якого не
 * зачіпає жодне правило, обидва завершують перерахунок УСПІХОМ і не рахують
 * нічого. Мовчазний нуль у звіті — найдорожчий різновид дефекту тут.
 *
 * ⚠ Панель стоїть на тому самому екрані, що й решта змісту версії, а не за
 * вкладкою — рішення екрана (коментар у `MethodologyVersionsPage.tsx`).
 * Макет `mv-binding` із набору (`docs/design/hybrid/screens-data.js`) дав
 * підписи станів («No rule», «Two rules, same priority») і саму думку про
 * колонку стану; сітку «колонка на кожне правило» з нього НЕ взято — контракт
 * сервера повертає комбінації значень, а не матрицю правил.
 */
export function MethodologyRuleCoveragePanel({
  methodologyId,
  versionId,
}: {
  readonly methodologyId: number;
  readonly versionId: number;
}): JSX.Element {
  const initial = useMemo(() => defaultRuleCoverageWindow(new Date()), []);

  const [tableDefId, setTableDefId] = useState<number | null>(initial.tableDefId);
  const [periodFrom, setPeriodFrom] = useState<number>(initial.periodFrom);
  const [periodTo, setPeriodTo] = useState<number>(initial.periodTo);

  const windowEmpty = ruleCoverageWindowEmpty(periodFrom, periodTo);

  // ⚠ Перелік таблиць береться з прив'язок методології — тих самих, якими
  // сервер обмежує вибірку. Інше джерело давало б у списку таблиці, за якими
  // матриця завжди порожня.
  const bindings = useQuery({
    queryKey: ['methodologies', 'bindings', methodologyId],
    queryFn: () => calculationBindings(methodologyId),
  });

  const tables = useMemo(() => {
    const ids = (bindings.data ?? [])
      .filter((binding) => binding.isActive)
      .map((binding) => binding.tableDefId);

    return [...new Set(ids)].sort((left, right) => left - right);
  }, [bindings.data]);

  const coverage = useQuery({
    queryKey: ['methodologies', 'ruleCoverage', versionId, tableDefId, periodFrom, periodTo],
    queryFn: () => ruleCoverage(methodologyId, versionId, { tableDefId, periodFrom, periodTo }),

    // ⛔ Порожнє вікно НЕ йде мережею: сервер відповів би на нього `422`
    // (`ECR-CALC-0422`), і єдиним наслідком кола до сервера була б затримка
    // перед тим самим текстом, який клієнт уже може назвати сам.
    enabled: !windowEmpty,
  });

  return (
    <>
      <Group justify="space-between">
        <Text fw={600}>{t('methodologies.ruleCoverage')}</Text>
      </Group>

      <Text size="sm" c="dimmed">
        {t('methodologies.ruleCoverageHint')}
      </Text>

      <Group gap="sm" align="flex-start">
        {/* ⛔ Вибір звужує, а не наповнює: «усі таблиці» — це саме те, що
            сервер робить без параметра, тож доки перелік прив'язок не приїхав
            (або не приїхав узагалі), матриця лишається робочою. Порожній
            список, який виглядав би як «таблиць немає», тут не виникає. */}
        <Select
          size="xs"
          miw={200}
          label={t('methodologies.ruleCoverageTable')}
          data={[
            { value: '', label: t('methodologies.ruleCoverageAllTables') },
            ...tables.map((id) => ({
              value: String(id),
              label: t('methodologies.ruleCoverageTableOption', { id }),
            })),
          ]}
          value={tableDefId === null ? '' : String(tableDefId)}
          onChange={(value) => {
            setTableDefId(value === null || value === '' ? null : Number(value));
          }}
        />

        <NumberInput
          size="xs"
          miw={140}
          label={t('methodologies.ruleCoveragePeriodFrom')}
          description={t('methodologies.ruleCoveragePeriodHint')}
          value={periodFrom}
          onChange={(value) => {
            if (typeof value === 'number') setPeriodFrom(value);
          }}
        />

        <NumberInput
          size="xs"
          miw={140}
          label={t('methodologies.ruleCoveragePeriodTo')}
          description={t('methodologies.ruleCoveragePeriodHint')}
          value={periodTo}
          onChange={(value) => {
            if (typeof value === 'number') setPeriodTo(value);
          }}
        />
      </Group>

      {/* ⚠ Текст відмови — ТОЙ САМИЙ ключ каталогу, що його віддає сервер
          (`err.ECR-CALC-0422.coverageWindow`), а не власний переказ: одна й
          та сама помилка, названа двома різними реченнями залежно від того,
          хто її помітив першим, — це два різні факти для того, хто читає. */}
      {windowEmpty && (
        <Banner
          tone="danger"
          testId="rule-coverage-window"
          title={t('err.ECR-CALC-0422.coverageWindow', { periodFrom, periodTo })}
        />
      )}

      {/* ⚠ Відмова переліку таблиць показується ОКРЕМО від матриці: матриця за
          «усіма таблицями» при цьому працює, і ховати її за чужою помилкою
          означало б показати порожньо там, де дані є. */}
      <ErrorAlert error={bindings.error} onRetry={() => void bindings.refetch()} />

      {!windowEmpty && (
        <AsyncBoundary<RuleCoverageDto>
          isPending={coverage.isPending}
          error={coverage.error}
          data={coverage.data}
          isEmpty={(matrix) => matrix.combinations.length === 0}
          emptyTitle={t('methodologies.noRuleCoverage')}
          emptyHint={t('methodologies.noRuleCoverageHint')}
          skeleton="table"
          onRetry={() => void coverage.refetch()}
        >
          {(matrix) => (
            <Stack gap="xs">
              {/* ⛔ Усічення називається ЗАВЖДИ, коли воно сталося. Стеля —
                  5000 комбінацій, і розрив може бути саме в тій частині,
                  якої не видно; мовчки показана частина відповідає на
                  питання «чи все покрито» неправдою. */}
              {matrix.truncated && (
                <Banner
                  tone="warning"
                  testId="rule-coverage-truncated"
                  title={t('methodologies.ruleCoverageTruncatedTitle')}
                  text={t('methodologies.ruleCoverageTruncatedHint', {
                    shown: matrix.combinations.length,
                  })}
                />
              )}

              {/* ⚠ Порядок рядків — серверний (розриви → конфлікти →
                  покриті, далі за кількістю рядків). Клієнт його НЕ
                  пересортовує: сервер бачить усі комбінації, клієнт — лише
                  ті, що влізли під стелю, тож його сортування збрехало б
                  саме на усіченій відповіді. */}
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('methodologies.ruleCoverageValues')}</Table.Th>
                    <Table.Th>{t('methodologies.ruleCoverageState')}</Table.Th>
                    <Table.Th>{t('methodologies.ruleCoverageRules')}</Table.Th>
                    <Table.Th>{t('methodologies.ruleCoverageRows')}</Table.Th>
                    <Table.Th>{t('methodologies.ruleCoverageDocuments')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {matrix.combinations.map((combination, index) => (
                    // ⚠ `data-combination` — не декорація: код правила й
                    // значення комірки бувають однаковим рядком (`NOX`
                    // збігається з `NOX`), тож знайти рядок таблиці за текстом
                    // не можна однозначно ні тесту, ні людині з читалкою.
                    <Table.Tr
                      key={`${combination.values.join(' ')}-${String(index)}`}
                      data-combination={combination.values.join('|')}
                    >
                      <Table.Td>
                        <ValueCells values={combination.values} />
                      </Table.Td>
                      <Table.Td>
                        <StateBadge state={combination.state} />
                      </Table.Td>
                      <Table.Td>
                        <RuleCell combination={combination} />
                      </Table.Td>
                      <Table.Td>{combination.rows}</Table.Td>
                      <Table.Td>{combination.documents}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Stack>
          )}
        </AsyncBoundary>
      )}
    </>
  );
}
