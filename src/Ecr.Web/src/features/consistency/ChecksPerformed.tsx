import { useId, type JSX } from 'react';
import { Badge, Group, Paper, SimpleGrid, Text, Title, UnstyledButton } from '@mantine/core';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { ruleLabel } from './consistencyRules';

/** Підсумок одного правила останнього прогону (`GET /consistency/summary` → `lastRun.checks`). */
export type ConsistencyCheckTally = components['schemas']['ConsistencyCheckTally'];

interface ChecksPerformedProps {
  /** Правила останнього завершеного прогону в порядку сервера. */
  readonly checks: readonly ConsistencyCheckTally[];

  /** Код правила з фільтра сторінки; `null` — усі. */
  readonly active: string | null;

  /** Клік по правилу — фільтр журналу за ним (повторний клік знімає фільтр). */
  readonly onSelect: (ruleCode: string | null) => void;
}

/**
 * «Перевірки останнього прогону»: кожне правило — людською назвою, з кількістю знахідок.
 *
 * ⛔ Аудит 24.11: «0 / 0 / 0» на смузі не казали, ЩО саме перевірено, і людина не могла відрізнити «перевірка нічого
 * не знайшла» від «перевірка нічого не дивилась». Тепер видно кожне правило проходу: знайдено N, пропущено (умова
 * вимкнена) чи перевірено частково (уперлося в стелю — старі знахідки правила не видаляються).
 */
export function ChecksPerformed({ checks, active, onSelect }: ChecksPerformedProps): JSX.Element {
  const headingId = useId();

  return (
    <Paper withBorder p="sm" mb="sm" component="section" aria-labelledby={headingId} data-checks-performed>
      <Title order={2} size="h6" id={headingId} mb="xs">
        {t('consistency.checks.title', { count: String(checks.length) })}
      </Title>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="xs" verticalSpacing="xs" role="list">
        {checks.map((check) => {
          const selected = active === check.ruleCode;
          return (
            <div key={check.ruleCode} role="listitem" data-rule={check.ruleCode}>
              <UnstyledButton
                onClick={() => onSelect(selected ? null : check.ruleCode)}
                aria-pressed={selected}
                w="100%"
                px="xs"
                bg={selected ? 'var(--mantine-color-default-hover)' : 'transparent'}
              >
                <Group gap="xs" wrap="nowrap" justify="space-between">
                  <Text size="sm" lineClamp={2}>
                    {ruleLabel(check.ruleCode)}
                  </Text>
                  <CheckState check={check} />
                </Group>
              </UnstyledButton>
            </div>
          );
        })}
      </SimpleGrid>
    </Paper>
  );
}

/** Стан правила: пропущено / частково / кількість знахідок. */
function CheckState({ check }: { readonly check: ConsistencyCheckTally }): JSX.Element {
  if (check.skipped) {
    return (
      <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>
        {t('consistency.checks.skipped')}
      </Text>
    );
  }

  return (
    <Group gap="xs" wrap="nowrap">
      {!check.complete && (
        <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>
          {t('consistency.checks.partial')}
        </Text>
      )}
      <Badge
        size="sm"
        variant="light"
        color={check.found > 0 ? 'statusWarning' : 'gray'}
        aria-label={t('consistency.checks.found', { count: String(check.found) })}
      >
        {check.found}
      </Badge>
    </Group>
  );
}
