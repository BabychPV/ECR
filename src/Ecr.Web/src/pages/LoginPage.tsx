import { useState, type JSX } from 'react';
import { Button, Card, Center, Divider, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { LocalLoginRequest } from '@/api/types';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { loadCatalog, preferredLanguage, t } from '@/shared/i18n';
import { useEffect } from 'react';

/**
 * Вхід: доменний і локальний.
 *
 * ⚠ Обидва способи видають **ту саму cookie** і той самий профіль. Різні
 * механізми сесії для двох способів входу означали б, що половина перевірок
 * безпеки працює лише для однієї половини користувачів.
 */
export function LoginPage(): JSX.Element {
  const navigate = useNavigate();
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  // Публічний каталог рядків тягнеться ДО входу: сторінка входу не може
  // показувати ключі замість написів (D-114).
  useEffect(() => {
    void loadCatalog(preferredLanguage(), 'public');
  }, []);

  // ⚠ Поле зветься `userName`, а не `login`: так називає його
  // `LocalLoginRequest`. До наскрізного аудиту клієнт надсилав `login`, і
  // сервер відповідав 400 «The UserName field is required» — вхід не
  // працював узагалі (`A7-09`).
  async function submit(path: string, body?: unknown): Promise<void> {
    setBusy(true);
    setError(null);

    try {
      await apiFetch(path, {
        method: 'POST',
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });

      navigate('/', { replace: true });
    } catch (failure) {
      // ⚠ Текст помилки — від сервера. Невірний пароль і неіснуючий
      // користувач дають ОДНАКОВУ відповідь: різниця між ними — це спосіб
      // перебрати логіни.
      setError(failure);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Center h="100vh">
      <Card withBorder w={380} p="lg">
        <Title order={3} mb="md">
          {t('login.title')}
        </Title>

        <Stack gap="sm">
          <Button onClick={() => void submit('/api/v1/login/windows')} loading={busy}>
            {t('login.windows')}
          </Button>

          <Divider label={t('login.or')} labelPosition="center" />

          <TextInput
            label={t('login.user')}
            value={login}
            onChange={(event) => setLogin(event.currentTarget.value)}
            autoComplete="username"
          />

          <PasswordInput
            label={t('login.password')}
            value={password}
            onChange={(event) => setPassword(event.currentTarget.value)}
            autoComplete="current-password"
          />

          <Button
            variant="default"
            loading={busy}
            onClick={() => void submit('/api/v1/login/local', { userName: login, password } satisfies LocalLoginRequest)}
          >
            {t('login.submit')}
          </Button>

          <ErrorAlert error={error} />

          <Text size="xs" c="dimmed">
            {t('login.hint')}
          </Text>
        </Stack>
      </Card>
    </Center>
  );
}
