import type { JSX } from 'react';
import { Anchor, Badge, Group, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import type { UnitRef } from '@/api/types';
import { UsageKindLabel } from '@/features/usage/UsageKindLabel';
import { decimalEquals, formatDecimal } from '@/shared/format';
import { dimensionLabel } from './dimensionLabel';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { KeyValue, type KeyValueItem } from '@/shared/ui/KeyValue';
import { problemText } from '@/shared/ui/problemText';
import { getUnit, unitUsage } from './api';

/**
 * Вміст шторки одиниці (UI-21; макет `screens-data.js` `/admin/units`,
 * `drawer`: Dimension · Base unit · Conversion · Offset · Where used).
 *
 * ⚠ ЛІНИВИЙ чанк: шторка закрита за замовчуванням (`L2`), і її вміст не
 * повинен важити в маршруті переліку.
 *
 * ⛔ Позначення, назва і «Where used» — лише з правом `Uom.EditCatalog`:
 * `GET /units/{id}` і `GET /units/{id}/usage` вимагають саме його
 * (`UnitUsageHandler`, `GetUnitHandler`). Без права ці частини НЕ малюються
 * (`D15-06`), а не показують відмову: людина без права нічого не зламала.
 * Перелік `GET /units` позначення й назви не віддає — TODO-контракт у листі
 * готовності (UnitRef.symbol/name, лічильник використань).
 */
export default function UnitDetailBody({
  unit,
  baseCode,
  canEdit,
}: {
  readonly unit: UnitRef;
  readonly baseCode: string | null;
  readonly canEdit: boolean;
}): JSX.Element {
  const detail = useQuery({
    queryKey: ['units', unit.id, 'detail'],
    queryFn: () => getUnit(unit.id),
    enabled: canEdit,
  });

  const usage = useQuery({
    queryKey: ['units', unit.id, 'usage'],
    queryFn: () => unitUsage(unit.id),
    enabled: canEdit,
    staleTime: 0,
  });

  const isBase = baseCode === unit.code;
  const factor = formatDecimal(unit.factorToBase) ?? unit.factorToBase;
  const offset = formatDecimal(unit.offsetToBase) ?? unit.offsetToBase;
  const hasOffset = !decimalEquals(unit.offsetToBase, '0');

  const items: KeyValueItem[] = [];

  if (detail.data !== undefined) {
    items.push(
      { label: t('units.symbol'), value: localized({ values: detail.data.symbolL10n }) },
      { label: t('units.name'), value: localized({ values: detail.data.nameL10n }) },
    );
  }

  items.push(
    { label: t('units.dimension'), value: dimensionLabel(unit.dimensionCode) },
    { label: t('units.baseUnit'), value: isBase ? `${unit.code} (${t('units.base')})` : (baseCode ?? '') },
  );

  // ⚠ «1 t = 1 000 kg» — формула з рядків сервера, без арифметики на клієнті:
  // множник і є відповіддю, а зсув дописується окремо (лише температура).
  if (!isBase && baseCode !== null) {
    items.push({
      label: t('units.conversion'),
      value: `1 ${unit.code} = ${factor} ${baseCode}${hasOffset ? ` + ${offset}` : ''}`,
      mono: true,
    });
  }

  if (hasOffset) {
    items.push({ label: t('units.offset'), value: offset, mono: true, hint: t('units.offsetHint') });
  }

  return (
    <Stack gap="md">
      <KeyValue items={items} />

      {canEdit && (
        <Stack gap="xs" data-testid="unit-where-used">
          <Title order={3} size="h5">
            {t('units.whereUsed')}
          </Title>

          {usage.isPending && (
            <Text size="sm" c="dimmed">
              {t('units.deleteChecking')}
            </Text>
          )}

          {usage.error !== null && (
            <Text size="sm" c="statusError">
              {problemText(usage.error).detail ?? problemText(usage.error).title}
            </Text>
          )}

          {usage.data !== undefined &&
            (usage.data.total === 0 ? (
              <Text size="sm" c="dimmed">
                {t('units.deleteUnused')}
              </Text>
            ) : (
              usage.data.items.map((item) => (
                <Group gap="xs" wrap="nowrap" key={`${item.kind}:${item.id}`}>
                  <Badge size="xs" variant="light">
                    <UsageKindLabel kind={item.kind} />
                  </Badge>
                  {item.route !== null ? (
                    <Anchor component={Link} to={item.route} size="sm">
                      {item.label}
                    </Anchor>
                  ) : (
                    <Text size="sm">{item.label}</Text>
                  )}
                </Group>
              ))
            ))}
        </Stack>
      )}
    </Stack>
  );
}
