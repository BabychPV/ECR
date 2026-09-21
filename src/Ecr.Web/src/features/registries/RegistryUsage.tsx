import type { JSX } from 'react';
import { Anchor, Badge, Group, List, Skeleton, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { registryUsage, type UsageResponse } from '@/features/registries/api';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';

/**
 * Ключ кешу «де використано» довідника.
 *
 * ⚠ Тут, а не в `api/queryKeys.ts`: той файл спільний (foundation), а ця
 * вкладка — єдиний споживач. Форма та сама, що в фабриці (`['registries', …]`),
 * тож `queryKeys.registries.all()` інвалідовує й цей запис.
 */
export function registryUsageKey(code: string): readonly ['registries', 'usage', string] {
  return ['registries', 'usage', code] as const;
}

/**
 * Вкладка «Де використано» конструктора довідника (`BE-24`, директива №15).
 *
 * ⛔ Порядок гілок той самий, що в `AsyncBoundary`: `error` → `isPending` →
 * дані. Відмова НЕ є «ніде не використано» (`L10`): на цьому твердженні людина
 * вирішує, що опис можна міняти вільно, і сказати його на відмові запиту —
 * найдорожча з можливих помилок.
 *
 * ⚠ `AsyncBoundary` не береться: вона малює власний `<Title order={4}>`, а
 * вкладка стоїть під заголовком сторінки — порядок заголовків
 * (`heading-order`, гейти `a11y`) розірвався б. Той самий вибір, що у вкладці
 * історії.
 */
export function RegistryUsagePanel({ code }: { code: string }): JSX.Element {
  const usage = useQuery({
    queryKey: registryUsageKey(code),
    queryFn: () => registryUsage(code),
  });

  if (usage.error !== null) {
    return <ErrorAlert error={usage.error} onRetry={() => void usage.refetch()} />;
  }

  if (usage.isPending) {
    return <Skeleton height={120} radius="sm" data-registry-usage="pending" />;
  }

  return <RegistryUsageList usage={usage.data} />;
}

/**
 * Перелік залежних із чесною кількістю.
 *
 * ⚠ `items` — не більше двадцяти перших, `total` — усі (`UsageResponse.PageSize`
 * на сервері). «Показано N із M» малюється лише коли `total > items.length`, і
 * лише з цими двома числами (`D15-06`): інакше перелік читався б як «оце й усе».
 *
 * ⚠ `route === null` — окремого екрана в об'єкта немає, і адреса не
 * вигадується: елемент лишається текстом.
 */
export function RegistryUsageList({ usage }: { usage: UsageResponse }): JSX.Element {
  if (usage.total === 0 && usage.items.length === 0) {
    return (
      <Text size="sm" data-registry-usage="none">
        {t('registries.usageNone')}
      </Text>
    );
  }

  return (
    <Stack gap="xs" data-registry-usage="list">
      <Text size="sm" fw={600}>
        {t('registries.usageTotal', { total: usage.total })}
      </Text>

      {usage.total > usage.items.length && (
        <Text size="sm" c="dimmed" data-registry-usage="truncated">
          {t('registries.usageShown', { shown: usage.items.length, total: usage.total })}
        </Text>
      )}

      <List listStyleType="none" spacing="xs">
        {usage.items.map((item) => (
          <List.Item key={`${item.kind}:${item.id}`}>
            <Group gap="xs" wrap="nowrap">
              <Badge size="xs" variant="light">
                {item.kind}
              </Badge>

              {item.route === null ? (
                <Text size="sm">{item.label}</Text>
              ) : (
                <Anchor component={Link} to={item.route} size="sm">
                  {item.label}
                </Anchor>
              )}

              {/* ⚠ Ідентифікатор — рядок сервера як є: без роздільників розрядів. */}
              <Text size="xs" c="dimmed">
                {item.id}
              </Text>
            </Group>
          </List.Item>
        ))}
      </List>
    </Stack>
  );
}
