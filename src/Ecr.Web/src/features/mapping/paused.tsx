import type { JSX } from 'react';
import { Badge } from '@mantine/core';
import type { MappedFieldPreview, MappingPreview } from '@/api/types';
import { toneFills } from '@/shared/ui/StatusBadge';
import { t } from '@/shared/i18n';

/**
 * Призупинений мапінг у перегляді (`BE-27`).
 *
 * ⛔ Сервер віддає в `fields` і призупинені мапінги (`isActive: false`), але
 * адреси рядків і `unmappedSourceFields` рахує **лише за діючими**:
 * призупинений значень не пише. Клієнт мусить триматися того самого правила,
 * інакше екран показуватиме мапінг діючим або лічитиме розривом те, що людина
 * вимкнула свідомо.
 *
 * ⚠ Призупинений мапінг — НЕ розрив, навіть із `NoData`: пауза — рішення
 * людини, як і `RawOnly` (`D-118`), а не дефект, який треба шукати.
 */

/** Діючі мапінги — лише вони кладуть значення в документ. */
export function activeFields(fields: readonly MappedFieldPreview[]): MappedFieldPreview[] {
  return fields.filter((field) => field.isActive);
}

/** Лічильники мапінгів за тим самим правилом, що й сервер. */
export function mappingCounts(fields: readonly MappedFieldPreview[]): {
  active: number;
  paused: number;
} {
  const active = activeFields(fields).length;

  return { active, paused: fields.length - active };
}

/**
 * Перегляд, у якому для розривів лишено лише діючі мапінги.
 *
 * ⚠ `unmappedSourceFields` не чіпається: поле, в якого є лише призупинений
 * мапінг, сервер уже поклав у «йде нікуди», і клієнт не має права вважати
 * його покритим через саму наявність запису в `fields`.
 */
export function gapsView(preview: MappingPreview): MappingPreview {
  return { ...preview, fields: activeFields(preview.fields) };
}

/**
 * Позначка «призупинено».
 *
 * ⚠ Локальна, а не `StatusBadge`: у наборі немає різновиду для мапінгу. Тон
 * узято з тієї самої таблиці токенів (`toneFills.muted`), тож контраст
 * виміряний, а не підібраний тут.
 */
export function PausedBadge(): JSX.Element {
  return (
    <Badge
      size="sm"
      miw="fit-content"
      variant="default"
      bg={toneFills.muted.bg}
      c={toneFills.muted.text}
      data-mapping-paused="true"
    >
      {t('mapping.paused')}
    </Badge>
  );
}
