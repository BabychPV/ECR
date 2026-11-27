import { useEffect, useRef, useState, type JSX } from 'react';
import { Alert, Button, Group, PasswordInput, Select, Stack, Text, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import type { DataSource, SaveDataSourceBody } from './dataSourceApi';
import { CredentialsStateKeys } from './credentialsStates';

type Transport = SaveDataSourceBody['transport'];
export type AuthMode = 'Windows' | 'UserPassword';

/** Облікові дані джерела в чернетці форми. ⛔ `password` — лише те, що людина ввела ЦЬОГО разу. */
export interface CredentialsDraft {
  authMode: AuthMode;
  userName: string;
  password: string;
  /** Людина натиснула «Clear»: сервер зітре пароль і логін (разом із переходом на Windows). */
  clearPassword: boolean;
  /** Людина відкрила порожнє поле «Change password» поверх збереженого пароля. */
  changingPassword: boolean;
}

/** Вхід за логіном і паролем підтримують лише PiSqlClient і Sql (сервер: `dataSourceCredentialsTransport`). */
export function supportsCredentials(transport: Transport): boolean {
  return transport !== 'PiWebApi';
}

export function credentialsDraftOf(source: DataSource | null): CredentialsDraft {
  return {
    authMode: source?.authMode ?? 'Windows',
    userName: source?.userName ?? '',
    password: '',
    clearPassword: false,
    changingPassword: false,
  };
}

/**
 * Зміна адреси, транспорту чи логіна на джерелі з ЗБЕРЕЖЕНИМ паролем потребує пароля ще раз (AC-7): інакше
 * збережений пароль пішов би на чужий хост. Порівняння буквальне — ширше за серверне; остаточне слово за сервером
 * (`dataSourcePasswordReentryRequired`).
 */
export function needsPasswordReentry(
  draft: {
    readonly transport: Transport;
    readonly endpoint: string;
    readonly secondaryEndpoint: string;
  } & CredentialsDraft,
  source: DataSource | null,
): boolean {
  if (source === null || source.authMode !== 'UserPassword' || !source.hasPassword) return false;
  if (draft.authMode !== 'UserPassword' || draft.clearPassword) return false;

  return (
    draft.transport !== source.transport ||
    draft.endpoint.trim() !== source.endpoint.trim() ||
    draft.secondaryEndpoint.trim() !== (source.secondaryEndpoint ?? '').trim() ||
    draft.userName.trim() !== (source.userName ?? '').trim()
  );
}

const PasswordId = 'source-password';

/**
 * Блок «Authentication» форми з'єднання (PiSqlClient, Sql). PiWebApi перемикача не має.
 *
 * ⛔ Пароль — ЛИШЕ запис: сервер віддає `hasPassword`, а не значення. Збережений пароль показується статичним
 * «•••• set» (не залежить від довжини), а поле вводу завжди починається порожнім — `value` ніколи не береться
 * з даних сервера.
 */
export function SourceCredentialsFields({
  source,
  draft,
  passwordRequired,
  passwordError,
  userNameError,
  stateKey,
  onChange,
}: {
  readonly source: DataSource | null;
  readonly draft: CredentialsDraft;
  /** Пароль обов'язковий: нове з'єднання, зміна цілі чи логіна, стан «введіть заново». */
  readonly passwordRequired: boolean;
  readonly passwordError: string | undefined;
  readonly userNameError: string | undefined;
  /** Ключ стану облікових даних із відмови (`CredentialsStateKeys`); `null` — стану немає. */
  readonly stateKey: string | null;
  readonly onChange: (patch: Partial<CredentialsDraft>) => void;
}): JSX.Element {
  const [confirmingWindows, setConfirmingWindows] = useState(false);
  const passwordRef = useRef<HTMLInputElement>(null);

  const stored = source !== null && source.authMode === 'UserPassword' && source.hasPassword;
  const userPassword = draft.authMode === 'UserPassword';
  const showStored = userPassword && stored && !draft.changingPassword && !passwordRequired && !draft.clearPassword;
  const showInput = userPassword && !showStored;

  // Відмова «введіть пароль заново» ставить фокус на поле.
  const asked = passwordError !== undefined || stateKey !== null;
  useEffect(() => {
    if (asked || draft.changingPassword) passwordRef.current?.focus();
  }, [asked, draft.changingPassword]);

  const chooseMode = (value: string | null): void => {
    if (value === null || value === draft.authMode) return;
    if (value === 'Windows') {
      // Збережені логін і пароль зникнуть — лише з підтвердженням.
      if (source !== null && source.authMode === 'UserPassword') setConfirmingWindows(true);
      else onChange({ authMode: 'Windows', password: '', userName: '', changingPassword: false });
    } else {
      onChange({ authMode: 'UserPassword', clearPassword: false });
    }
  };

  return (
    <Stack gap="sm" data-credentials="">
      <Select
        label={t('sources.authMode')}
        description={t('sources.authModeHint')}
        data={[
          { value: 'Windows', label: t('sources.authMode.Windows') },
          { value: 'UserPassword', label: t('sources.authMode.UserPassword') },
        ]}
        value={draft.authMode}
        onChange={chooseMode}
        allowDeselect={false}
        data-field="authMode"
      />

      {userPassword && (
        <TextInput
          label={t('sources.userName')}
          description={t('sources.userNameHint')}
          value={draft.userName}
          onChange={(event) => onChange({ userName: event.currentTarget.value })}
          error={userNameError}
          required
          maxLength={256}
          autoComplete="off"
          spellCheck={false}
          data-field="userName"
        />
      )}

      {showStored && (
        <Stack gap="xs" data-password-stored="">
          <Text size="sm" fw={500}>
            {t('sources.password')}
          </Text>
          <Group gap="xs" align="center">
            <Text size="sm" ff="monospace" data-password-set="">
              {t('sources.passwordSet')}
            </Text>
            <Button variant="default" size="xs" onClick={() => onChange({ changingPassword: true })} data-change-password="">
              {t('sources.changePassword')}
            </Button>
            <Button
              variant="subtle"
              color="statusError"
              size="xs"
              onClick={() => setConfirmingWindows(true)}
              data-clear-password=""
            >
              {t('sources.clearPassword')}
            </Button>
          </Group>
        </Stack>
      )}

      {showInput && (
        <PasswordInput
          label={t('sources.password')}
          description={t('sources.passwordHint')}
          ref={passwordRef}
          id={PasswordId}
          value={draft.password}
          onChange={(event) => onChange({ password: event.currentTarget.value })}
          error={passwordError}
          required={passwordRequired}
          maxLength={256}
          autoComplete="new-password"
          // ⚠ `PasswordInput` Mantine не зв'язує текст помилки з внутрішнім полем — робимо це самі, як у SMTP.
          aria-invalid={passwordError !== undefined}
          aria-describedby={
            passwordError === undefined ? `${PasswordId}-description` : `${PasswordId}-description ${PasswordId}-error`
          }
          data-field="password"
        />
      )}

      {stateKey !== null && (
        <Alert color="statusError" data-credentials-state={stateKey}>
          {t(CredentialsStateKeys[stateKey] === 'sources.credentialsRejected' ? 'sources.credentialsRejected' : 'sources.credentialsUnreadable')}
        </Alert>
      )}

      <ConfirmModal
        opened={confirmingWindows}
        title={t('sources.switchToWindows')}
        text={t('sources.switchToWindowsConfirm')}
        verb={t('sources.switchToWindows')}
        onClose={() => setConfirmingWindows(false)}
        onConfirm={() => {
          setConfirmingWindows(false);
          onChange({ authMode: 'Windows', password: '', userName: '', clearPassword: true, changingPassword: false });
        }}
      />
    </Stack>
  );
}
