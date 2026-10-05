import { useEffect, useRef, useState, type JSX } from 'react';
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
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
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

/** Стабільний `id` поля пароля: Mantine будує з нього `-description` і `-error`. */
const SmtpPasswordId = 'smtp-password';

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
  const passwordRef = useRef<HTMLInputElement>(null);

  /*
   * ⛔ L9-33: незбережена чернетка захищена так само, як матриця правил і шаблони: перехід
   * маршрутом питає `UnsavedGuard` (джерело існує, лише поки є чернетка; `flush` немає — мовчазний
   * `PUT` налаштувань пошти при переході неприйнятний), закриття вкладки — штатне питання браузера.
   * Доти вихід зі сторінки мовчки губив введені хост, адресу й пароль.
   */
  const dirty = draft !== null;
  useEffect(() => {
    if (!dirty) return undefined;

    return registerUnsavedSource('smtp-settings', {
      hasUnsaved: () => true,
      unsavedCount: () => 1,
    });
  }, [dirty]);
  useEffect(() => {
    if (!dirty) return undefined;

    const warn = (event: BeforeUnloadEvent): void => {
      event.preventDefault();
      // Старі браузери показують питання лише за непорожнього `returnValue`.
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);

    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  // ⚠ Відмова «введіть пароль ще раз» — фокус у поле пароля: тост зникає, а читач екрана
  // інакше не знає, ДЕ виправляти (поле вже має `aria-invalid` і опис помилки).
  useEffect(() => {
    if (passwordHint !== null) passwordRef.current?.focus();
  }, [passwordHint]);

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
  // ⛔ ent6 S1: пароль без шифрування сервер відхиляє (`smtpPasswordNeedsTls`) — кажемо це біля поля
  // шифрування ДО збереження, а не лише тостом після відмови.
  const passwordNeedsTls = form.authMode === 'Password' && form.encryptionMode === 'None';
  // ⛔ L9-28: на час `PUT` форма замкнена. `onSuccess` знімає чернетку цілком (`setDraft(null)`), тож
  // правка між кліком «Зберегти» і відповіддю мовчки зникала б: у тілі запиту її не було.
  const locked = save.isPending;
  const portOk = portValid(form.port);

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
        <TextInput label={t('smtp.host')} readOnly={locked} value={form.host} onChange={(e) => set({ host: e.currentTarget.value })} />
        <NumberInput
          label={t('smtp.port')}
          value={form.port}
          readOnly={locked}
          min={1}
          max={65535}
          allowDecimal={false}
          allowNegative={false}
          error={portOk ? undefined : t('err.ECR-REQ-0422.smtpSettingsInvalid', { name: t('smtp.port') })}
          // ⛔ L9-32: значення поля — як ввела людина. Доти порожнє поле миттю ставало `587` (стерти й
          // набрати інший порт було неможливо — цифри дописувались до 587), а `70000` до втрати фокуса
          // (Mantine обрізає лише на blur) їхало на сервер і поверталось загальною відмовою.
          onChange={(value) => set({ port: value })}
        />
        <Select
          label={t('smtp.encryption')}
          allowDeselect={false}
          data={[
            { value: 'StartTls', label: t('smtp.encryption.StartTls') },
            { value: 'None', label: t('smtp.encryption.None') },
          ]}
          value={form.encryptionMode}
          disabled={locked}
          error={passwordNeedsTls ? t('err.ECR-REQ-0422.smtpPasswordNeedsTls') : null}
          onChange={(value) => {
            if (value !== null) set({ encryptionMode: value as SmtpDraft['encryptionMode'] });
          }}
        />
      </Group>

      <Group grow align="flex-start">
        <TextInput
          label={t('smtp.from')}
          value={form.fromAddress}
          readOnly={locked}
          onChange={(e) => set({ fromAddress: e.currentTarget.value })}
        />
        <TextInput
          label={t('smtp.fromName')}
          value={form.fromName}
          readOnly={locked}
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
        disabled={locked}
        onChange={(value) => {
          if (value !== null) set({ authMode: value as SmtpDraft['authMode'] });
        }}
      />

      {form.authMode === 'Password' && (
        <Group grow align="flex-start">
          <TextInput
            label={t('smtp.user')}
            value={form.userName}
            readOnly={locked}
            onChange={(e) => set({ userName: e.currentTarget.value })}
          />
          <PasswordInput
            label={t('smtp.password')}
            description={settings.data.hasPassword ? t('smtp.passwordStored') : t('smtp.passwordNone')}
            autoComplete="new-password"
            ref={passwordRef}
            id={SmtpPasswordId}
            error={passwordHint}
            // ⚠ `PasswordInput` Mantine не ставить внутрішньому полю ні `aria-invalid`, ні посилання на
            // опис і помилку (`TextInput` ставить) — читач екрана не чув би ні «збережено», ні відмови.
            aria-invalid={passwordHint !== null}
            aria-describedby={
              passwordHint === null ? `${SmtpPasswordId}-description` : `${SmtpPasswordId}-description ${SmtpPasswordId}-error`
            }
            value={form.password}
            readOnly={locked}
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
          disabled={locked}
          onChange={(e) => set({ clearPassword: e.currentTarget.checked })}
        />
      )}

      <Switch
        label={t('smtp.enabled')}
        checked={form.isEnabled}
        disabled={locked}
        onChange={(e) => set({ isEnabled: e.currentTarget.checked })}
      />

      <Group justify="flex-end">
        <Button
          loading={saveLoading}
          disabled={draft === null || !portOk}
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
            // ⚠ Друга проба поверх першої — другий лист і зайвий крок квоти проб (`SmtpTestRateLimitPolicy`;
            // `D-263` про квоту не каже — лише про те, що SMTP налаштовується в системі).
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
  /** Як у полі: число, або рядок (порожньо чи незавершене введення) — тоді зберегти не можна. */
  readonly port: number | string;
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

/** Порт, який прийме сервер за формою (`SaveSmtpSettingsHandler.Validate`): ціле 1–65535. */
export function portValid(port: number | string): boolean {
  return typeof port === 'number' && Number.isInteger(port) && port >= 1 && port <= 65535;
}

/** Порожні рядки їдуть як `null`; порожній пароль — «не змінювати». */
function inputOf(draft: SmtpDraft): SmtpSettingsInput {
  const blank = (v: string): string | null => (v.trim().length === 0 ? null : v.trim());

  return {
    host: blank(draft.host),
    port: Number(draft.port),
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
