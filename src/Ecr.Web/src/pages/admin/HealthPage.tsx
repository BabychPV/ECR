import type { JSX } from 'react';
import { Badge, Button, Card, Group, SimpleGrid, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import type { HealthReport } from '@/api/types';
import { formatDateTime } from '@/shared/format';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { KeyValue } from '@/shared/ui/KeyValue';
import { showApiError, showDone } from '@/shared/ui/notify';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

type SystemFacts = components['schemas']['SystemFactsResponse'];

/**
 * Команда для DBA → буфер обміну (`BE-18`, рішення `D15-12`).
 *
 * ⛔ Кнопка НЕ створює партицій: застосунок не виконує DDL (`D-66`). Вона лише
 * кладе в буфер текст, який сервер віддає як `text/plain`.
 *
 * ⚠ Голий `fetch`, а не `apiFetch`: той розбирає тіло як JSON і на простому
 * тексті впав би. Відмова однаково показується — і мережева, і буфера обміну
 * (без HTTPS або дозволу `navigator.clipboard` недоступний чи кидає).
 */
async function copyPartitionScript(): Promise<void> {
  try {
    const response = await fetch('/api/v1/health/partitions/script', { credentials: 'include' });

    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`.trim());

    await navigator.clipboard.writeText(await response.text());
    showDone(t('health.partitionScriptCopied'));
  } catch (error) {
    showApiError(error);
  }
}

/**
 * Операційний дашборд.
 *
 * ⚠ `/health/db` віддає **подробиці**: редакцію SQL Server, стан RCSI,
 * файлові групи, запас партицій і перелік того, що в цьому режимі недоступне
 * (АРХ-7 п. 5). Зведений «зелений» без цих полів був би обіцянкою, яку не
 * можна перевірити: система на Express формально жива і при цьому не вміє
 * половини того, на що розрахований регламент.
 */
export function HealthPage(): JSX.Element {
  const ready = useQuery({
    queryKey: ['health', 'ready'],
    queryFn: () => apiFetch<HealthReport>('/health/ready'),
    refetchInterval: 30_000,
  });

  const db = useQuery({
    queryKey: ['health', 'db'],
    queryFn: () => apiFetch<HealthReport>('/health/db'),
    refetchInterval: 60_000,
  });

  // ⚠ Без `refetchInterval`: версія, час старту й середовище не міняються, доки
  // процес живий, а перезапуск сторінка й так побачить на наступному відкритті.
  const facts = useQuery({
    queryKey: ['health', 'facts'],
    queryFn: () => apiFetch<SystemFacts>('/api/v1/health/facts'),
  });

  return (
    <>
      <PageHeader
        title={t('health.title')}
        actions={
          ready.data === undefined ? null : (
            <Badge color={badgeColor(ready.data.status)}>{ready.data.status}</Badge>
          )
        }
      />

      {/*
       * ⛔ Через `<AsyncBoundary>`, а не через `?? {}`. Саме `?? {}` і був
       * `A7-04`: невдалий запит давав порожній дашборд, а порожній дашборд і
       * здорова система виглядали однаково. Тут порожньо ≠ помилка (`ФВ-14.22`).
       */}
      <AsyncBoundary<HealthReport>
        isPending={ready.isPending}
        error={ready.error}
        data={ready.data}
        isEmpty={(report) => report.checks.length === 0}
        emptyTitle={t('health.noChecks')}
        emptyHint={t('health.noChecksHint')}
        onRetry={() => void ready.refetch()}
      >
        {(report) => (
          <SimpleGrid cols={{ base: 1, md: 3 }} mb="lg">
            {report.checks.map((check) => (
              <Card key={check.name} withBorder>
                <Group justify="space-between">
                  <Text fw={600}>{check.name}</Text>
                  <Badge color={badgeColor(check.status)} variant="light">
                    {check.status}
                  </Badge>
                </Group>
                {check.description !== null && (
                  <Text size="sm" mt="xs">
                    {check.description}
                  </Text>
                )}
              </Card>
            ))}
          </SimpleGrid>
        )}
      </AsyncBoundary>

      {/* ⚠ Довідкові факти, не вміст екрана: доки їх немає (ще вантажаться,
          відмова, відповідь не тієї форми) — секція не малюється взагалі
          (`D15-06`), а про справжню біду вже кажуть дві межі поруч. */}
      {typeof facts.data?.productVersion === 'string' && (
        <Stack gap="xs" mb="lg" data-health-facts="">
          <Text fw={600}>{t('health.facts')}</Text>
          <KeyValue wide items={factItems(facts.data)} />
        </Stack>
      )}

      <Stack gap="xs">
        <Group justify="space-between">
          <Text fw={600}>{t('health.database')}</Text>
          <Button variant="default" size="xs" onClick={() => void copyPartitionScript()}>
            {t('health.copyPartitionScript')}
          </Button>
        </Group>

        {/* ⚠ Обмеження режиму показуються переліком, а не ховаються: саме за
            ними видно, чому вночі не працює архівація або чому немає запасу
            партицій. */}
        <AsyncBoundary<HealthReport>
          isPending={db.isPending}
          error={db.error}
          data={db.data}
          isEmpty={(report) => details(report) === null}
          emptyTitle={t('health.noDbDetails')}
          skeleton="table"
          onRetry={() => void db.refetch()}
        >
          {(report) => (
            <Table striped withTableBorder>
              <Table.Tbody>
                {Object.entries(details(report) ?? {}).map(([key, value]) => (
                  <Table.Tr key={key}>
                    <Table.Td miw={200}>{fieldLabel(key)}</Table.Td>
                    <Table.Td>{fieldValue(value)}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          )}
        </AsyncBoundary>
      </Stack>
    </>
  );
}

/**
 * Факти про процес у вигляді пар для `KeyValue`.
 *
 * ⛔ «Не налаштовано» — це ТЕКСТ, а не пропущений рядок: відсутній транспорт
 * означає, що сповіщення накопичуються в черзі й нікуди не йдуть, і саме це
 * адміністратор має прочитати. А от «налаштовано» без виду транспорту рядка не
 * дає — називати нема чого (пару без значення `KeyValue` не малює).
 */
function factItems(facts: SystemFacts): { label: string; value: string | null }[] {
  const transport = facts.notificationTransport;

  return [
    { label: t('health.facts.productVersion'), value: facts.productVersion },
    { label: t('health.facts.startedAt'), value: formatDateTime(facts.startedAt) },
    { label: t('health.facts.environment'), value: facts.environment },
    {
      label: t('health.facts.notificationTransport'),
      value: transport.isConfigured ? transport.kind : t('health.facts.transportNotConfigured'),
    },
  ];
}

/**
 * Подробиці перевірки бази.
 *
 * ⚠ Пошук за іменем `db`, а не за позицією: `/health/db` сьогодні містить одну
 * перевірку, але позиційне звернення розсипалося б мовчки від першої ж другої.
 */
function details(report: HealthReport): Record<string, unknown> | null {
  const check = report.checks.find((candidate) => candidate.name === 'db');

  return check === undefined || Object.keys(check.data).length === 0 ? null : check.data;
}

/**
 * Технічне ім'я поля `/health/db` → ключ каталогу з людським підписом.
 *
 * ⛔ Аудит-пас 8, lane6, п.7: до фіксу рядок панелі показував буквально
 * `edition`, `effectiveMode`, `rcsi` тощо (`DatabaseHealthCheck.cs` — сталий
 * camelCase-словник) — не текст із каталогу з іншою мовою, а взагалі не
 * підпис. Дев'ять полів фіксовані контрактом `/health/db`, тому мапа тут, а
 * не вгадування: невідоме поле показує сам ключ (той самий принцип запасного
 * варіанту, що й `reasonOf` у `permissions.ts` — краще показати ім'я поля,
 * ніж вигадати підпис).
 */
const FieldLabelKeys: Record<string, string> = {
  edition: 'health.database.edition',
  effectiveMode: 'health.database.effectiveMode',
  majorVersion: 'health.database.majorVersion',
  rcsi: 'health.database.rcsi',
  archiveBatchSize: 'health.database.archiveBatchSize',
  filegroups: 'health.database.filegroups',
  missingFilegroups: 'health.database.missingFilegroups',
  partitionsAhead: 'health.database.partitionsAhead',
  limitations: 'health.database.limitations',
};

function fieldLabel(key: string): string {
  const translationKey = FieldLabelKeys[key];

  return translationKey === undefined ? key : t(translationKey);
}

/**
 * Знак «значення є, і воно порожнє» (UI-прохід, F7).
 *
 * ⛔ Порожня клітинка читається ДВОЯКО: «відсутніх файлових груп немає» і «цей
 * рядок не завантажився». Це той самий клас, що `A7-04` вище на цій же
 * сторінці, лише на один рядок дрібніший: порожнеча ≠ помилка мусить бути
 * видно, а не додумуватись (`ФВ-14.22`).
 *
 * ⚠ Тире, а не `t('…')`: рядки цього застосунку йдуть із серверного каталогу
 * (`09-seed.sql`), ключа під «немає» там немає, а голий `t()` без рядка показав
 * би `⟦…⟧` — тобто замінив би одну незрозумілу клітинку на іншу. Той самий
 * аргумент, що в `passwordToggleProps` (`pages/LoginPage.tsx`). Знак
 * нейтральний до мови, тож заміна його рядком каталогу пізніше нічого тут не
 * перебудовує.
 */
const EmptyValue = '—';

/**
 * Значення поля `/health/db` у вигляді, придатному для клітинки.
 *
 * ⛔ Сирий `String(value)` і був дефектом: `missingFilegroups` і `limitations`
 * — це СПИСКИ (`DatabaseHealthCheck.cs`, `data["missingFilegroups"] = missing`),
 * а `String([])` — порожній рядок. Здорова система (жодної відсутньої групи,
 * жодного обмеження режиму) малювала два порожні рядки серед восьми
 * заповнених. Непорожній список він же зліплював без пробілів (`A,B`).
 */
function fieldValue(value: unknown): string {
  if (Array.isArray(value)) {
    return value.length === 0 ? EmptyValue : value.map(String).join(', ');
  }

  if (value === null || value === undefined) return EmptyValue;

  const text = String(value);

  return text.trim().length === 0 ? EmptyValue : text;
}

/**
 * Колір статусу.
 *
 * ⛔ Три стани, а не два. `Degraded` — це не «помилка»: система працює, але
 * чогось у ній бракує. Показувати його червоним означало б навчити оператора
 * не дивитися на червоне.
 */
function badgeColor(status: string): string {
  if (status === 'Healthy') return 'statusSuccess';

  return status === 'Degraded' ? 'statusWarning' : 'statusError';
}
