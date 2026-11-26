import { useState, type JSX } from 'react';
import { Button, Stack, Textarea } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { testConnectionFromBody, type TestConnectionBody } from './dataSourceApi';
import { OutcomeBanner, outcomeOf } from './TestDataSourceModal';

/**
 * «Test connection» за значеннями НЕЗБЕРЕЖЕНОЇ форми (DESIGN §13.1): нічого не зберігає.
 *
 * ⚠ Причина обов'язкова (проба йде в журнал безпеки) — власне поле, як у діалозі перевірки збереженого з'єднання.
 * ⚠ Тіло збирає власник форми в момент натиску (`build`), а не ззовні знімком: пароль живе лише в чернетці.
 */
export function FormTestConnection({
  build,
  disabled,
  onEnterPassword,
}: {
  /** Тіло без причини: форма знає свої поля, цей блок — лише причину. */
  readonly build: () => Omit<TestConnectionBody, 'reason'>;
  readonly disabled: boolean;
  readonly onEnterPassword: () => void;
}): JSX.Element {
  const [reason, setReason] = useState('');

  const test = useMutation({
    // ⚠ Відмову показує `ErrorAlert` нижче — без `handled` сітка додала б тост (L9-01).
    meta: { handled: true },
    mutationFn: (value: string) => testConnectionFromBody({ ...build(), reason: value }),
  });

  const trimmed = reason.trim();

  return (
    <Stack gap="xs" data-form-test="">
      <Textarea
        label={t('sources.testReason')}
        description={t('sources.testReasonHint')}
        value={reason}
        onChange={(event) => setReason(event.currentTarget.value)}
        minRows={2}
        autosize
      />

      {test.data !== undefined && <OutcomeBanner outcome={outcomeOf(test.data)} onEnterPassword={onEnterPassword} />}

      {test.error !== null && <ErrorAlert error={test.error} />}

      <Button
        variant="default"
        disabled={disabled || trimmed.length === 0}
        loading={test.isPending}
        onClick={() => test.mutate(trimmed)}
        data-form-test-run=""
      >
        {t('sources.testConnection')}
      </Button>
    </Stack>
  );
}
