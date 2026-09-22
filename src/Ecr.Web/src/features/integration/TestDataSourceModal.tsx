import { useEffect, useState, type JSX } from 'react';
import { Alert, Button, Group, Modal, Stack, Text, Textarea } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { formatCount } from '@/shared/format';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { problemText } from '@/shared/ui/problemText';
import { testDataSource, type DataSourceTestResult } from './dataSourceApi';

/**
 * Чим закінчилася проба з'єднання — три РІЗНІ відповіді, а не «успіх/помилка».
 *
 * ⛔ `failed` — це `200` із `ok: false`: канал відповів, і відповідь — відмова
 * джерела. Показати її як збій запиту (червоний `ErrorAlert` із кодом
 * кореляції) означало б сказати «щось зламалося в нас», коли зламалося там.
 *
 * ⚠ `running` — `409`: проба цього з'єднання вже йде. Це не відмова джерела й
 * не збій — просто чекати.
 */
export type TestOutcome =
  | { readonly kind: 'ok'; readonly entities: number }
  | { readonly kind: 'failed'; readonly reason: string | null }
  | { readonly kind: 'running'; readonly detail: string | null };

/**
 * Відповідь сервера → те, що показати.
 *
 * ⚠ Причина відмови — з каталогу за `messageKey`, коли сервер його назвав, і
 * сирим `error` — коли ні (той самий порядок, що в `ChannelsPanel`): власного
 * «не вдалося» тут бути не може, бо причину знає лише джерело.
 */
export function outcomeOf(result: DataSourceTestResult): TestOutcome {
  if (result.ok) return { kind: 'ok', entities: result.entities };

  const key = result.messageKey ?? null;

  return { kind: 'failed', reason: key !== null && key.length > 0 ? t(key) : result.error };
}

/**
 * Код відмови «стан не дозволяє дію» (`ErrorCodes.JobStateConflict`): на цьому
 * виклику — проба цього з'єднання вже йде.
 */
const TestRunningCode = 'ECR-JOB-0409';

/**
 * `ECR-JOB-0409` на цьому виклику — «проба вже йде».
 *
 * ⚠ Гілка за КОДОМ, а не за статусом: клієнт розрізняє причини кодом
 * (`client.ts`), і `409` без нашого коду (проксі, шлюз) — не «проба йде», а
 * збій запиту. Подробиця — лише локалізована сервером (`problemText`).
 */
function runningOf(error: unknown): TestOutcome | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== TestRunningCode) return null;

  return { kind: 'running', detail: problemText(error).detail };
}

/**
 * Діалог «Test connection» (директива №15 §3 `UI-09`).
 *
 * ⛔ Причина ОБОВ'ЯЗКОВА: проба йде в журнал безпеки (сервер ходить у чужу
 * систему від імені службового запису). Кнопка вимкнена, доки причини немає
 * — увімкнена кнопка, яка гарантовано дасть `422`, обіцяла б те, чого не буде.
 *
 * ⚠ Власний діалог, а не `ReasonModal`: той ставить фокус у поле причини, а
 * `L6` вимагає фокус на Cancel; і результат проби треба показати ТУТ, поруч із
 * причиною, а не тостом, що зникне раніше, ніж його прочитають.
 */
export function TestDataSourceModal({
  opened,
  sourceId,
  sourceName,
  onClose,
}: {
  readonly opened: boolean;
  readonly sourceId: number;
  readonly sourceName: string;
  readonly onClose: () => void;
}): JSX.Element {
  const [reason, setReason] = useState('');

  const test = useMutation({
    mutationFn: (value: string) => testDataSource(sourceId, value),
  });

  const { reset } = test;

  // ⚠ Поле й попередній результат очищаються при КОЖНОМУ відкритті: зелений
  // банер минулої проби над новою причиною читався б як відповідь на неї.
  useEffect(() => {
    if (opened) {
      setReason('');
      reset();
    }
  }, [opened, reset]);

  const trimmed = reason.trim();

  const outcome: TestOutcome | null =
    test.data !== undefined ? outcomeOf(test.data) : runningOf(test.error);

  // Будь-яка інша відмова (403, 422, 5xx) — справжній збій запиту.
  const failure = test.error !== null && outcome === null ? test.error : null;

  return (
    <Modal opened={opened} onClose={onClose} title={t('sources.testTitle', { name: sourceName })}>
      <Stack gap="sm">
        <Textarea
          label={t('sources.testReason')}
          description={t('sources.testReasonHint')}
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          minRows={2}
          autosize
        />

        {outcome !== null && <OutcomeBanner outcome={outcome} />}

        {failure !== null && <ErrorAlert error={failure} />}

        <Group justify="flex-end" gap="xs">
          {/* ⛔ `L6`: фокус на Cancel, і лише тут. */}
          <Button variant="default" data-autofocus onClick={onClose}>
            {t('common.cancel')}
          </Button>

          <Button
            disabled={trimmed.length === 0}
            loading={test.isPending}
            onClick={() => test.mutate(trimmed)}
          >
            {t('sources.testConnection')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Банер результату.
 *
 * ⚠ Mantine `Alert` сам ставить `role="alert"` (власний `role` він
 * перебиває): результат приходить асинхронно, і жива область тут доречна —
 * без неї читалка не дізналася б, що проба завершилася. Відрізняє банер від
 * збою запиту (`ErrorAlert`) не роль, а `data-test-outcome` і відсутність
 * коду кореляції.
 */
function OutcomeBanner({ outcome }: { readonly outcome: TestOutcome }): JSX.Element {
  switch (outcome.kind) {
    case 'ok':
      return (
        <Alert color="statusSuccess" title={t('sources.testOk')} data-test-outcome="ok">
          {formatCount(outcome.entities, 'sources.testEntities')}
        </Alert>
      );

    case 'failed':
      return (
        <Alert color="statusError" title={t('sources.testFailed')} data-test-outcome="failed">
          {/* `D15-06`: причини немає — немає й порожнього рядка. */}
          {outcome.reason !== null && outcome.reason.length > 0 && (
            <Text size="sm">{outcome.reason}</Text>
          )}
        </Alert>
      );

    case 'running':
      return (
        <Alert color="statusWarning" title={t('sources.testRunning')} data-test-outcome="running">
          {outcome.detail !== null && outcome.detail.length > 0 && (
            <Text size="sm">{outcome.detail}</Text>
          )}
        </Alert>
      );
  }
}
