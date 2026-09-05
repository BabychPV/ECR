import type { JSX } from 'react';
import { Badge, Card, Group, SimpleGrid, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Одна перевірка у звіті.
 *
 * ⛔ Форма описана руками, і це виняток, а не правило: `/health/*` — не
 * контролер, а middleware, тому його немає в OpenAPI і згенерувати тип нема з
 * чого. Саме через це тут двічі жив `A7-04`: опис не збігався з відповіддю, і
 * ніщо про це не сказало.
 *
 * ⚠ Тому нижче — `checks` МАСИВОМ із полем `name`, точно як пише
 * `HealthResponse.WriteAsync`, і тест `health.test.tsx` тримає обидві форми
 * поруч на зразку справжньої відповіді сервера.
 */
interface HealthCheck {
  name: string;
  status: string;
  description: string | null;
  durationMs: number;
  data: Record<string, unknown>;
}

interface HealthReport {
  status: string;
  totalDurationMs: number;
  checks: HealthCheck[];
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

      <Stack gap="xs">
        <Text fw={600}>{t('health.database')}</Text>

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
                    <Table.Td miw={200}>{key}</Table.Td>
                    <Table.Td>{String(value)}</Table.Td>
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
 * Колір статусу.
 *
 * ⛔ Три стани, а не два. `Degraded` — це не «помилка»: система працює, але
 * чогось у ній бракує. Показувати його червоним означало б навчити оператора
 * не дивитися на червоне.
 */
function badgeColor(status: string): string {
  if (status === 'Healthy') return 'green';

  return status === 'Degraded' ? 'yellow' : 'red';
}
