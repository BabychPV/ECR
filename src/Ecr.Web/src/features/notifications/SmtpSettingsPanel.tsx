import { useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Group,
  NumberInput,
  PasswordInput,
  Select,
  Skeleton,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { EcrApiError } from '@/api/client';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  getSmtpSettings,
  saveSmtpSettings,
  SmtpSettingsKey,
  testSmtpSettings,
  type SmtpSettings,
  type SmtpSettingsInput,
} from './api';
import { showProbeResult } from './probeResult';

/** Ключ відмови `422`: адресу змінено, а збережений пароль не підтверджено (`SaveSmtpSettingsHandler`). */
const SmtpPasswordReentryRequiredKey = 'err.ECR-REQ-0422.smtpPasswordReentryRequired';

/**
 * Налаштування SMTP, які адміністратор задає в системі (`D-263`, `GET/PUT /notifications/smtp`).
 *
 * ⛔ Пароль — ЛИШЕ запис: сервер віддає `hasPassword`, а не значення, тож поле завжди порожнє, а
 * порожнє означає «не змінювати». Відправляється тільки те, що людина ввела цього разу.
 *
 * ⚠ «Надіслати тест» шле лист через ЕФЕКТИВНІ налаштування (збережені, інакше конфігурацію процесу)
 * — тож перед перевіркою нових значень їх треба зберегти. Відмова транспорту показується категорією
 * з каталогу (`notifications.test.smtp.*`), а не сирим текстом сервера, коли категорія відома.
 */
export function SmtpSettingsPanel(): JSX.Element {
  const queryClient = useQueryClient();
  const settings = useQuery({
    queryKey: SmtpSettingsKey,
    queryFn: getSmtpSettings,
  });
  const [draft, setDraft] = useState<SmtpDraft | null>(null);
  const [testTo, setTestTo] = useState('');
  const [passwordHint, setPasswordHint] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: (value: SmtpDraft) => saveSmtpSettings(inputOf(value)),
    onMutate: () => setPasswordHint(null),
    onSuccess: async (saved) => {
      queryClient.setQueryData(SmtpSettingsKey, saved);
      setDraft(null);
      showDone(t('smtp.saved'));
    },
    onError: (error) => {
      // ⛔ S1 (ent6): адресу змінено, а збережений пароль не переноситься — підсвічуємо поле пароля.
      if (
        error instanceof EcrApiError &&
        error.problem.status === 422 &&
        error.problem.extensions2?.['messageKey'] === SmtpPasswordReentryRequiredKey
      ) {
        setPasswordHint(t('err.ECR-REQ-0422.smtpPasswordReentryRequired'));
      }
      showApiError(error);
    },
  });

  const test = useMutation({
    mutationFn: (to: string) => testSmtpSettings(to),
    onSuccess: showProbeResult,
    onError: showApiError,
  });

  const saveLoading = usePendingLoading(save.isPending);
  const testLoading = usePendingLoading(test.isPending);

  const title = (
    <Title order={2} size="h5">
      {t('smtp.title')}
    </Title>
  );

  if (settings.error !== null) {
    return (
      <Stack gap="sm">
        {title}
        <ErrorAlert error={settings.error} onRetry={() => void settings.refetch()} />
      </Stack>
    );
  }

  if (settings.isPending) {
    return (
      <Stack gap="sm">
        {title}
        <Skeleton height={160} radius="sm" data-smtp="pending" />
      </Stack>
    );
  }

  const form = draft ?? draftOf(settings.data);
  const set = (patch: Partial<SmtpDraft>): void => setDraft({ ...form, ...patch });

  return (
    <Stack gap="sm">
      {title}
      <Text size="sm" c="dimmed">
        {t('smtp.hint')}
      </Text>
      <Group gap="xs">
        <Badge
          color={settings.data.configured ? 'statusSuccess' : 'statusWarning'}
          variant="light"
          data-smtp-source={settings.data.source}
        >
          {sourceLabel(settings.data.source)}
        </Badge>
      </Group>

      <Group grow align="flex-start">
        <TextInput label={t('smtp.host')} value={form.host} onChange={(e) => set({ host: e.currentTarget.value })} />
        <NumberInput
          label={t('smtp.port')}
          value={form.port}
          min={1}
          max={65535}
          allowDecimal={false}
          onChange={(value) => set({ port: typeof value === 'number' ? value : 587 })}
        />
        <Select
          label={t('smtp.encryption')}
          allowDeselect={false}
          data={[
            { value: 'StartTls', label: t('smtp.encryption.StartTls') },
            { value: 'None', label: t('smtp.encryption.None') },
          ]}
          value={form.encryptionMode}
          onChange={(value) => {
            if (value !== null) set({ encryptionMode: value as SmtpDraft['encryptionMode'] });
          }}
        />
      </Group>

      <Group grow align="flex-start">
        <TextInput
          label={t('smtp.from')}
          value={form.fromAddress}
          onChange={(e) => set({ fromAddress: e.currentTarget.value })}
        />
        <TextInput
          label={t('smtp.fromName')}
          value={form.fromName}
          onChange={(e) => set({ fromName: e.currentTarget.value })}
        />
      </Group>

      <Select
        label={t('smtp.auth')}
        allowDeselect={false}
        data={[
          { value: 'None', label: t('smtp.auth.None') },
          { value: 'Password', label: t('smtp.auth.Password') },
        ]}
        value={form.authMode}
        onChange={(value) => {
          if (value !== null) set({ authMode: value as SmtpDraft['authMode'] });
        }}
      />

      {form.authMode === 'Password' && (
        <Group grow align="flex-start">
          <TextInput
            label={t('smtp.user')}
            value={form.userName}
            onChange={(e) => set({ userName: e.currentTarget.value })}
          />
          <PasswordInput
            label={t('smtp.password')}
            description={settings.data.hasPassword ? t('smtp.passwordStored') : t('smtp.passwordNone')}
            autoComplete="new-password"
            error={passwordHint}
            value={form.password}
            onChange={(e) => {
              setPasswordHint(null);
              set({ password: e.currentTarget.value });
            }}
          />
        </Group>
      )}

      {form.authMode === 'Password' && settings.data.hasPassword && (
        <Checkbox
          label={t('smtp.clearPassword')}
          checked={form.clearPassword}
          onChange={(e) => set({ clearPassword: e.currentTarget.checked })}
        />
      )}

      <Switch
        label={t('smtp.enabled')}
        checked={form.isEnabled}
        onChange={(e) => set({ isEnabled: e.currentTarget.checked })}
      />

      <Group justify="flex-end">
        <Button
          loading={saveLoading}
          disabled={draft === null}
          onClick={() => {
            // ⚠ До порогу `usePendingLoading` кнопка ще активна: другий клік не шле другий PUT.
            if (save.isPending) return;
            save.mutate(form);
          }}
        >
          {t('smtp.save')}
        </Button>
      </Group>

      <Group align="flex-end">
        <TextInput
          label={t('smtp.testTo')}
          value={testTo}
          onChange={(e) => setTestTo(e.currentTarget.value)}
          style={{ flex: 1 }}
        />
        <Button
          variant="default"
          loading={testLoading}
          disabled={testTo.trim().length === 0 || draft !== null}
          onClick={() => {
            // ⚠ Друга проба поверх першої — другий лист і зайвий крок квоти проб (`D-263`).
            if (test.isPending) return;
            test.mutate(testTo.trim());
          }}
        >
          {t('smtp.testSend')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис джерела транспорту; літерали, а не шаблонний ключ — сторож ключів бачить кожен рядок. */
function sourceLabel(source: string): string {
  if (source === 'database') return t('smtp.source.database');
  if (source === 'configuration') return t('smtp.source.configuration');

  return t('smtp.source.none');
}

/** Чернетка форми: рядки й булеві; `password` — лише те, що введено ЦЬОГО разу. */
interface SmtpDraft {
  readonly host: string;
  readonly port: number;
  readonly encryptionMode: 'None' | 'StartTls';
  readonly fromAddress: string;
  readonly fromName: string;
  readonly authMode: 'None' | 'Password';
  readonly userName: string;
  readonly password: string;
  readonly clearPassword: boolean;
  readonly isEnabled: boolean;
}

function draftOf(value: SmtpSettings): SmtpDraft {
  return {
    host: value.host,
    port: value.port,
    encryptionMode: value.encryptionMode,
    fromAddress: value.fromAddress,
    fromName: value.fromName ?? '',
    authMode: value.authMode,
    userName: value.userName ?? '',
    password: '',
    clearPassword: false,
    isEnabled: value.isEnabled,
  };
}

/** Порожні рядки їдуть як `null`; порожній пароль — «не змінювати». */
function inputOf(draft: SmtpDraft): SmtpSettingsInput {
  const blank = (v: string): string | null => (v.trim().length === 0 ? null : v.trim());

  return {
    host: blank(draft.host),
    port: draft.port,
    encryptionMode: draft.encryptionMode,
    fromAddress: blank(draft.fromAddress),
    fromName: blank(draft.fromName),
    authMode: draft.authMode,
    userName: blank(draft.userName),
    password: draft.password.length === 0 ? null : draft.password,
    clearPassword: draft.clearPassword,
    isEnabled: draft.isEnabled,
  };
}
