import type { JSX } from 'react';
import { useState } from 'react';
import { Badge, Group, Stack, Table, Text, Title } from '@mantine/core';
import type { MappingPreview } from '@/api/types';
import { DeleteMappingAction } from './DeleteMappingAction';
import { target } from './MappingGaps';
import { outcomeColor, outcomeLabel } from './outcome';
import { PauseResumeAction } from './PauseResumeAction';
import { PausedBadge, mappingCounts } from './paused';
import { UnitChangeAction } from './UnitChangeAction';
import { toneFills } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

/**
 * Ключ «банер зміни одиниці для цього мапінгу закрито в цьому сеансі».
 *
 * ⚠ `detectedAt`, а не лише `fieldMapId`: якщо збір ще раз помітить зміну
 * одиниці ПІСЛЯ того, як людина закрила попередній банер («Ні, це помилка
 * джерела»), це вже НОВА подія з новим часом виявлення — і вона має право на
 * власний банер, а не мовчазне приховання через збіг id.
 */
function pendingBannerKey(fieldMapId: number, detectedAtUtc: string): string {
  return `${String(fieldMapId)}:${detectedAtUtc}`;
}

/**
 * Мапінги і реальні рядки джерела (`ФВ-13.14`).
 *
 * ⛔ Спершу — що ляже в комірку, потім — рядки, з яких це число вийшло.
 * Порядок не косметичний: питання «чому в комірці саме це» без згорнутого
 * значення поруч із серією неможливо закрити, не викачуючи дані руками.
 *
 * ⚠ Значення показуються **в одиниці ДЖЕРЕЛА** — так вони й зберігаються
 * (`ФВ-16.10`, `D-79`). Тому одиниці стоять обидві: розбіжність між ними і є
 * найчастіше джерело мовчазних розходжень у числах (`ФВ-16.9`).
 */
export function MappingRows({
  preview,
  allowed = false,
}: {
  readonly preview: MappingPreview;

  /**
   * Чи має сесія право `Integration.Manage` — те саме право, під яким на
   * сторінці ховається кнопка «Add mapping» (`MappingPreviewPage.tsx`).
   *
   * ⚠ За замовчуванням `false`, а не обов'язковий пропс: наявні тести й
   * `MappingGaps`-сусід рендерять таблицю без сесії взагалі, і без дефолту
   * кожен виклик довелося б чіпати заради пропса, який їм не потрібен.
   */
  readonly allowed?: boolean;
}): JSX.Element {
  const counts = mappingCounts(preview.fields);

  // ⚠ Стан живе ТУТ, а не в `UnitChangeAction`: закритий банер визначає, який
  // З ДВОХ рядків малювати для поля (банер чи звичайний paused), а не як
  // виглядає сам банер.
  const [dismissedPending, setDismissedPending] = useState<ReadonlySet<string>>(new Set());

  return (
    <Stack gap="lg">
      <Stack gap="xs">
        <Title order={2} size="h4">
          {t('mapping.maps')}
        </Title>

        {/* ⚠ Лише коли є пауза: «0 призупинених» під кожним справним
            мапінгом — шум, а діючі за сервером рахуються тими ж, що й тут. */}
        {counts.paused > 0 && (
          <Text size="sm" c="dimmed" data-testid="mapping-counts">
            {t('mapping.mapsSummary', { active: counts.active, paused: counts.paused })}
          </Text>
        )}

        <Table striped highlightOnHover className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('mapping.field')}</Table.Th>
              <Table.Th>{t('mapping.target')}</Table.Th>
              <Table.Th>{t('mapping.aggregation')}</Table.Th>
              <Table.Th>{t('mapping.units')}</Table.Th>
              <Table.Th>{t('mapping.points')}</Table.Th>
              <Table.Th>{t('mapping.folded')}</Table.Th>
              <Table.Th>{t('mapping.outcome')}</Table.Th>
              {/* ⚠ Без напису, як «actions» у `SourcesPage.tsx`: колонка
                  видима лише тим, у кого є право `Integration.Manage`, і
                  назва їй не потрібна — кнопка сама називає свою дію. */}
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {preview.fields.map((field) => {
              const pending = field.pendingSourceUnitChange;
              const bannerKey = pending === null ? null : pendingBannerKey(field.fieldMapId, pending.detectedAt);

              if (bannerKey !== null && !dismissedPending.has(bannerKey)) {
                return (
                  <Table.Tr key={field.fieldMapId} data-mapping-state="pending-unit-change">
                    <Table.Td>{field.sourceField}</Table.Td>
                    {/* ⚠ `colSpan={7}`: `1` (поле) + `7` = усі вісім колонок
                        заголовка. Одна клітинка, а не окремі порожні, — банер
                        показує СВОЄ, а не імітує решту рядка даними, яких
                        для призупиненого поля щойно немає. */}
                    <Table.Td colSpan={7}>
                      <UnitChangeAction
                        field={field}
                        allowed={allowed}
                        onDismiss={() =>
                          setDismissedPending((prev) => new Set(prev).add(bannerKey))
                        }
                      />
                    </Table.Td>
                  </Table.Tr>
                );
              }

              return field.isActive ? (
                <Table.Tr key={field.fieldMapId}>
                  <Table.Td>{field.sourceField}</Table.Td>
                  <Table.Td>{target(field.targetRowKey, field.targetColumnCode)}</Table.Td>
                  <Table.Td>{field.aggregation ?? '—'}</Table.Td>
                  <Table.Td>
                    {field.sourceUnitCode ?? '—'} → {field.targetUnitCode ?? '—'}
                  </Table.Td>
                  <Table.Td>{field.pointCount}</Table.Td>
                  <Table.Td>{field.foldedValue ?? '—'}</Table.Td>
                  <Table.Td>
                    <Badge color={outcomeColor(field.outcome)} variant="light">
                      {outcomeLabel(field.outcome)}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    {allowed && (
                      <Group gap="xs" wrap="nowrap" align="flex-start">
                        <PauseResumeAction field={field} />
                        <DeleteMappingAction field={field} />
                      </Group>
                    )}
                  </Table.Td>
                </Table.Tr>
              ) : (
                /* ⛔ Призупинений (`BE-27`) нікуди не пише: ні адреси, ні
                   «значення в комірці», ні стану «лягає в комірку» — усе це
                   читалося б як діючий мапінг. Лишаються поле, згортка,
                   одиниці й точки: вони пояснюють уже зібране. Приглушення —
                   лише токеном. */
                <Table.Tr
                  key={field.fieldMapId}
                  data-mapping-state="paused"
                  c={toneFills.muted.text}
                >
                  <Table.Td>{field.sourceField}</Table.Td>
                  <Table.Td>—</Table.Td>
                  <Table.Td>{field.aggregation ?? '—'}</Table.Td>
                  <Table.Td>
                    {field.sourceUnitCode ?? '—'} → {field.targetUnitCode ?? '—'}
                  </Table.Td>
                  <Table.Td>{field.pointCount}</Table.Td>
                  <Table.Td>—</Table.Td>
                  <Table.Td>
                    <PausedBadge />
                  </Table.Td>
                  <Table.Td>
                    {allowed && (
                      <Group gap="xs" wrap="nowrap" align="flex-start">
                        <PauseResumeAction field={field} />
                        <DeleteMappingAction field={field} />
                      </Group>
                    )}
                  </Table.Td>
                </Table.Tr>
              );
            })}
          </Table.Tbody>
        </Table>
      </Stack>

      <Stack gap="xs">
        <Title order={2} size="h4">
          {t('mapping.rows')}
        </Title>
        <Text size="sm" c="dimmed">
          {t('mapping.rowsHint')}
        </Text>

        <Table striped highlightOnHover className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('mapping.timestamp')}</Table.Th>
              <Table.Th>{t('mapping.field')}</Table.Th>
              <Table.Th>{t('mapping.value')}</Table.Th>
              <Table.Th>{t('mapping.quality')}</Table.Th>
              <Table.Th>{t('mapping.target')}</Table.Th>
              <Table.Th>{t('mapping.outcome')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {preview.rows.map((row, index) => (
              <Table.Tr key={`${row.sourcePath}|${row.timestamp}|${index}`}>
                {/* ⚠ `precise`: мітка ТОЧКИ ТЕЛЕМЕТРІЇ. Саме секундами вона
                    відрізняється від сусідньої, і без них два різні виміри в
                    одній хвилині виглядають однаково — тобто перегляд мапінгу
                    перестає показувати те, заради чого його відкривають.
                    ⚠ `key` рядка й далі бере СИРИЙ `row.timestamp`: ключ — для
                    React, а не для ока, і зав'язувати його на відформатований
                    текст означало б перестворювати рядки при зміні мови. */}
                <Table.Td>
                  <Timestamp value={row.timestamp} precise />
                </Table.Td>
                <Table.Td>{row.sourcePath}</Table.Td>
                <Table.Td>{row.valueNumeric ?? row.valueString ?? '—'}</Table.Td>
                <Table.Td>{row.quality ?? '—'}</Table.Td>
                <Table.Td>{target(row.targetRowKey, row.targetColumnCode)}</Table.Td>
                <Table.Td>
                  <Badge color={outcomeColor(row.outcome)} variant="light">
                    {outcomeLabel(row.outcome)}
                  </Badge>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Stack>
    </Stack>
  );
}
