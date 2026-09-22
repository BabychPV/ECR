import type { JSX } from 'react';
import { Alert, Anchor, Badge, Button, Group, Stack, Text } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { problemText } from '@/shared/ui/problemText';
import { Timestamp } from '@/shared/ui/Timestamp';
import { CatalogPermission, isSourceOutage } from './piafCatalogApi';
import {
  isProbePathNotFound,
  probeSourcePath,
  probeSuggestions,
  type SourcePathProbeResult,
} from './piafProbeApi';

/**
 * Кнопка «Перевірити» біля поля шляху мапінгу (`ФВ-13.17`): пробне читання
 * ОДНОГО значення в РЕАЛЬНОМУ джерелі, тим самим адаптером, яким потім
 * збиратимуть, і нічого не зберігає.
 *
 * ⛔ До цієї кнопки одруківка в шляху мапінгу виявлялася лише за місяць: збір
 * «успішно» завершувався нулем точок, а перегляд мапінгу не відрізняв такий
 * мапінг від справного (`MappingGaps.brokenHint`, `NoData`). «Перевірити»
 * ходить у джерело до того, як мапінг узагалі заведено.
 *
 * ⛔ Кнопки немає БЕЗ права `Integration.Manage` — те саме право, що вимагає
 * сервер, і те саме, що ховає кнопку каталогу (`PiAfCatalogButton`). Не заміна
 * серверній перевірці — та лишається єдиним рішенням.
 *
 * ⚠ Право `CatalogPermission` (`Integration.Manage`) навмисно те саме, що для
 * каталогу (`ФВ-13.13`): проба і каталог — дві дії на тому самому ресурсі
 * (з'єднанні), і різні права на них означали б, що людина бачить кнопку,
 * клік на яку сервер відмовить.
 */
export function PiAfProbeAction({
  dataSourceId,
  path,
  onPickSuggestion,
}: {
  readonly dataSourceId: number;

  /** Поточний (можливо, ще не збережений) шлях із поля форми. */
  readonly path: string;

  /** Підставляє підказку в поле шляху мапінгу — те саме поле, що читає `path`. */
  readonly onPickSuggestion: (value: string) => void;
}): JSX.Element | null {
  const session = useSession();

  const probe = useMutation({
    mutationFn: (value: string) => probeSourcePath(dataSourceId, value),
  });

  if (!can(session.data, CatalogPermission)) return null;

  const trimmed = path.trim();
  const failure = probe.error;
  const outage = failure !== null && isSourceOutage(failure);
  const notFound = failure !== null && isProbePathNotFound(failure);
  const otherFailure = failure !== null && !outage && !notFound;

  const run = (): void => {
    if (trimmed.length === 0) return;
    probe.mutate(trimmed);
  };

  return (
    <Stack gap="xs" align="flex-start">
      <Button size="xs" variant="default" disabled={trimmed.length === 0} loading={probe.isPending} onClick={run}>
        {t('mapping.probeAction')}
      </Button>

      {/*
        ⛔ Стан «джерело не відповідає» — ТОЙ САМИЙ, що в каталозі
        (`isSourceOutage`, `503`/`502`), і подається так само: окремий блок із
        причиною і «повторити», а не помилка під полем. Виправити мовчання
        чужої системи в полі шляху неможливо, і показ такої відмови там читався
        б як «ти ввела не те».
      */}
      {outage && (
        <Alert color="statusWarning" title={t('mapping.catalogUnavailable')} data-probe-state="unavailable">
          <Stack gap="xs" align="flex-start">
            <Text size="sm">{t('mapping.probeUnavailableHint')}</Text>

            {problemText(failure).detail !== null && <Text size="sm">{problemText(failure).detail}</Text>}

            <Button size="xs" variant="default" onClick={run}>
              {t('common.retry')}
            </Button>
          </Stack>
        </Alert>
      )}

      {/*
        ⛔ `404` з підказками — окремий стан, а не звичайна `ErrorAlert`: сама
        подія («шляху немає») передбачувана й ненайближчі кандидати ЦЬОГО Ж
        рівня — саме те, що допомагає виправити одруківку на місці, без
        повторного походу в каталог.
      */}
      {notFound && (
        <Alert color="statusError" title={problemText(failure).title} role="alert" data-probe-state="not-found">
          <Stack gap="xs" align="flex-start">
            {problemText(failure).detail !== null && <Text size="sm">{problemText(failure).detail}</Text>}

            {/* `L10`: порожній перелік підказок — блок не малюється взагалі. */}
            {probeSuggestions(failure).length > 0 && (
              <Stack gap="xs" align="flex-start" data-probe-suggestions>
                <Text size="xs" c="dimmed">
                  {t('mapping.probeSuggestionsHint')}
                </Text>

                <Group gap="xs">
                  {probeSuggestions(failure).map((suggestion) => (
                    <Anchor
                      key={suggestion}
                      component="button"
                      type="button"
                      size="sm"
                      onClick={() => onPickSuggestion(suggestion)}
                    >
                      {suggestion}
                    </Anchor>
                  ))}
                </Group>
              </Stack>
            )}
          </Stack>
        </Alert>
      )}

      {/* Решта відмов (403, 422, збій мережі) — звичайний збій запиту з кодом
          і кореляцією, показаний одразу під полем шляху й кнопкою. */}
      {otherFailure && <ErrorAlert error={failure} />}

      {probe.data !== undefined && <ProbeResult result={probe.data} />}
    </Stack>
  );
}

/**
 * Наслідок успішної проби: значення й контекст, або пояснення, чому значення
 * немає.
 */
function ProbeResult({ result }: { readonly result: SourcePathProbeResult }): JSX.Element {
  /*
   * ⛔ `hasValue: false` — це НЕ помилка й НЕ порожнеча без пояснення (`L10`):
   * шлях підтверджений каталогом, просто в пробному вікні (30 днів) для нього
   * немає жодної точки. Показ як «нічого немає» без цього речення читався б
   * як «шлях невірний» — а він вірний.
   */
  if (!result.hasValue) {
    return (
      <Text size="sm" c="dimmed" data-probe-state="no-data">
        {t('mapping.probeNoData')}
      </Text>
    );
  }

  const value = result.valueNumeric ?? result.valueString ?? '—';
  const unit = result.unitSymbol !== null && result.unitSymbol.length > 0 ? ` ${result.unitSymbol}` : '';

  return (
    <Stack gap="xs" align="flex-start" data-probe-state="value">
      <Badge variant="light" color="gray">
        {t('mapping.value')}: {value}
        {unit}
      </Badge>

      <Text size="xs" c="dimmed">
        {t('mapping.timestamp')}: <Timestamp value={result.timestamp} precise />
      </Text>

      {result.quality !== null && result.quality.length > 0 && (
        <Text size="xs" c="dimmed">
          {t('mapping.quality')}: {result.quality}
        </Text>
      )}
    </Stack>
  );
}
