import { useEffect, useRef, useState, type JSX } from 'react';
import { Badge, Button, Group, NumberInput, Stack, Table, Text, Title } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { formatDateTime, formatNumber } from '@/shared/format';
import { useFocusAfterBusy } from '@/shared/a11y/focus';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { AttributeScope } from './sourceEventMapForm';
import { probeSourceEvents, type ProbeSourceEventsResult } from './sourceEventsApi';

type ProbedEvent = ProbeSourceEventsResult['events'][number];

/** Межі проби на сервері (`probe-events`): вікно до 92 днів, до 100 подій. */
export const ProbeMaxDays = 92;
export const ProbeMaxEvents = 100;

/** Поле з таблицею відповідностей: які значення PI ще не зіставлені. */
export interface ProbeValueMapField {
  readonly key: string;
  readonly attribute: string;
  readonly scope: AttributeScope;
  readonly known: readonly string[];
}

/** Значення атрибута як текст: число — як є (десятковий рядок сервера), текст — як є. */
function attributeText(attribute: ProbedEvent['attributes'][number]): string | null {
  return attribute.valueString ?? attribute.valueNumeric;
}

/**
 * Значення атрибута `field` у пробі, яких ще немає в таблиці відповідностей, — у порядку появи, без повторів.
 */
export function unmatchedValues(events: readonly ProbedEvent[], field: ProbeValueMapField): string[] {
  const known = new Set(field.known.map((value) => value.trim()));
  const found: string[] = [];

  for (const event of events) {
    for (const attribute of event.attributes) {
      if (attribute.name !== field.attribute || attribute.scope !== field.scope) continue;
      const text = attributeText(attribute)?.trim();
      if (text === undefined || text.length === 0 || known.has(text) || found.includes(text)) continue;
      found.push(text);
    }
  }

  return found;
}

function utc(value: string | null): string {
  return value === null ? '' : formatDateTime(value, { dateStyle: 'short', timeStyle: 'medium', timeZone: 'UTC' });
}

/**
 * «Перевірити на реальних подіях» (FEATURE-HSE301-VIEW §10.6): `POST probe-events` за вікно назад від «зараз» —
 * події з розв'язаними атрибутами, НІЧОГО не пише. Для полів із таблицею відповідностей — значення PI, яких у
 * таблиці ще немає, з дією «Додати відповідність».
 *
 * ⚠ Атрибути запиту — лише ті, що вже в мапінгу (`attributes`); порожньо — сервер віддає всі, що дало джерело.
 */
export function SourceEventProbePanel({
  dataSourceId,
  template,
  attributes,
  valueMapFields,
  onAddValue,
}: {
  readonly dataSourceId: number;
  readonly template: string;
  readonly attributes: readonly { readonly name: string; readonly scope: AttributeScope }[];
  readonly valueMapFields: readonly ProbeValueMapField[];
  readonly onAddValue: (fieldKey: string, value: string) => void;
}): JSX.Element {
  const [days, setDays] = useState(30);
  const [maxEvents, setMaxEvents] = useState(20);

  const probe = useMutation({
    mutationFn: () => {
      const to = new Date();
      const from = new Date(to.getTime() - days * 24 * 60 * 60 * 1000);

      return probeSourceEvents(dataSourceId, {
        template,
        fromUtc: from.toISOString(),
        toUtc: to.toISOString(),
        attributes: attributes.length === 0 ? null : [...attributes],
        maxEvents,
      });
    },
  });

  const result = probe.data;

  // ⛔ «Перевірити» стає `loading` (= `disabled`) і втрачає фокус. Прийшов результат — фокус на його підсумок
  // (читач озвучує вікно й кількість подій, `Tab` іде далі в таблицю); відмова — назад на кнопку, поруч з
  // `ErrorAlert`. Інакше після кожної проби клавіатура починала з початку сторінки.
  const runFocus = useFocusAfterBusy(probe.isPending);
  const summary = useRef<HTMLParagraphElement>(null);

  useEffect(() => {
    if (result !== undefined) summary.current?.focus();
  }, [result]);

  return (
    <Stack gap="xs" data-source-event-probe="">
      <Title order={5}>{t('sourceEvents.probeTitle')}</Title>
      <Text size="xs" c="dimmed">
        {t('sourceEvents.probeHint')}
      </Text>

      <Group gap="sm" align="end">
        <NumberInput
          size="xs"
          miw={140}
          label={t('sourceEvents.probeDays')}
          min={1}
          max={ProbeMaxDays}
          value={days}
          onChange={(value) => setDays(typeof value === 'number' ? Math.min(Math.max(value, 1), ProbeMaxDays) : 30)}
          data-source-event-probe-days=""
        />
        <NumberInput
          size="xs"
          miw={140}
          label={t('sourceEvents.probeMaxEvents')}
          min={1}
          max={ProbeMaxEvents}
          value={maxEvents}
          onChange={(value) =>
            setMaxEvents(typeof value === 'number' ? Math.min(Math.max(value, 1), ProbeMaxEvents) : 20)
          }
          data-source-event-probe-max=""
        />
        <Button
          ref={runFocus.ref}
          size="xs"
          variant="default"
          loading={probe.isPending}
          onClick={() => {
            runFocus.arm();
            probe.mutate();
          }}
          data-source-event-probe-run=""
        >
          {t('sourceEvents.probeRun')}
        </Button>
      </Group>

      {probe.error !== null && <ErrorAlert error={probe.error} onRetry={() => probe.mutate()} />}

      {result !== undefined && (
        <Stack gap="xs" data-source-event-probe-result="">
          <Text ref={summary} tabIndex={-1} size="xs" c="dimmed" data-source-event-probe-summary="">
            {t('sourceEvents.probeWindow', { from: utc(result.fromUtc), to: utc(result.toUtc), count: formatNumber(result.events.length) })}
          </Text>

          {result.truncated && (
            <Banner tone="warning" text={t('sourceEvents.probeTruncated')} testId="source-event-probe-truncated" />
          )}
          {result.errorCode !== null && (
            <Banner
              tone="warning"
              text={t('sourceEvents.probePartial', { code: result.errorCode })}
              testId="source-event-probe-partial"
            />
          )}

          {valueMapFields.map((field) => {
            const unmatched = unmatchedValues(result.events, field);
            if (unmatched.length === 0) return null;

            return (
              <Group key={field.key} gap="xs" data-source-event-probe-unmatched={field.attribute}>
                <Text size="xs">{t('sourceEvents.probeUnmatched', { attribute: field.attribute })}</Text>
                {unmatched.map((value) => (
                  <Button
                    key={value}
                    size="compact-xs"
                    variant="light"
                    onClick={() => onAddValue(field.key, value)}
                    data-source-event-probe-add={value}
                  >
                    {t('sourceEvents.probeAddValue', { value })}
                  </Button>
                ))}
              </Group>
            );
          })}

          {result.events.length === 0 ? (
            <Text size="sm" c="dimmed" data-source-event-probe-empty="">
              {t('sourceEvents.probeEmpty')}
            </Text>
          ) : (
            // ⚠ У таблиці проби немає жодного фокусованого елемента, тож без `tabIndex` її горизонтальну
            // прокрутку клавіатурою не досягти (WCAG 2.1.1, axe `scrollable-region-focusable`). `native` — щоб
            // фокусованим був сам вузол, що прокручується, а не обгортка `ScrollArea` над ним.
            <Table.ScrollContainer
              minWidth={700}
              type="native"
              tabIndex={0}
              role="region"
              aria-label={t('sourceEvents.probeTitle')}
            >
              <Table data-source-event-probe-table="" aria-label={t('sourceEvents.probeTitle')}>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('sourceEvents.colTimeUtc')}</Table.Th>
                    <Table.Th>{t('sourceEvents.colName')}</Table.Th>
                    <Table.Th>{t('sourceEvents.colAttributes')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {result.events.map((event) => (
                    <Table.Tr key={event.eventId} data-source-event-probe-row={event.eventId}>
                      <Table.Td>
                        <Text size="xs">{utc(event.startUtc)}</Text>
                        <Text size="xs" c="dimmed">
                          {event.endUtc === null ? t('sourceEvents.stillOpen') : utc(event.endUtc)}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        <Text size="xs">{event.name ?? event.eventId}</Text>
                        {event.primaryElementPath !== null && (
                          <Text size="xs" c="dimmed" ff="monospace">
                            {event.primaryElementPath}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Stack gap="xs">
                          {event.attributes.map((attribute) => (
                            <Group key={`${attribute.scope}:${attribute.name}`} gap="xs" wrap="nowrap">
                              {attribute.scope === 'PrimaryElement' && (
                                <Badge size="xs" variant="outline" color="gray">
                                  {t('sourceEvents.scopePrimaryElement')}
                                </Badge>
                              )}
                              <Text size="xs" ff="monospace">
                                {attribute.name} = {attributeText(attribute) ?? t('sourceEvents.emptyValue')}
                                {attribute.sourceUnitSymbol === null ? '' : ` ${attribute.sourceUnitSymbol}`}
                              </Text>
                            </Group>
                          ))}
                        </Stack>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
        </Stack>
      )}
    </Stack>
  );
}
