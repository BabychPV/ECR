import { useState, type JSX } from 'react';
import {
  Button,
  Card,
  Center,
  Divider,
  Loader,
  PasswordInput,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { apiFetch, EcrApiError } from '@/api/client';
import type { LocalLoginRequest } from '@/api/types';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { isCatalogFailed, isCatalogResolved, loadCatalog, preferredLanguage, t } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { useEffect } from 'react';

/**
 * Помилка «каталог перекладів не завантажився» (`D-138`).
 *
 * ⛔ Текст ЖОРСТКО закодований, а не з `t()`: якщо не завантажився сам
 * каталог, будь-який виклик `t('...')` тут повернув би позначений ключ
 * (`⟦...⟧`) замість пояснення причини — рівно той дефект, від якого це
 * повідомлення й рятує. `EcrApiError` узято тому, що `ErrorAlert` показує
 * `problem.title`/`message` напряму, у ЦЕЙ каталог не заглядаючи.
 */
const CATALOG_LOAD_FAILED = new EcrApiError({
  title: 'Переклади інтерфейсу не завантажилися',
  status: 0,
  errorCode: 'ECR-I18N-CATALOG-FAILED',
  correlationId: '-',
  detail:
    'Не вдалося завантажити текстовий каталог інтерфейсу. Перевірте з’єднання з мережею та оновіть сторінку.',
});

/**
 * Аргументи для кнопки-тумблера видимості пароля (`Q-260`).
 *
 * ⛔ Mantine ставить на цю кнопку `aria-hidden="true"` і `tabIndex={-1}` за
 * замовчуванням, доки `visibilityToggleButtonProps` цього не перекриє
 * (`PasswordInput.mjs`) — сама наявність об'єкта вже знімає `aria-hidden`, а
 * явний `tabIndex: 0` повертає зупинку табом. Без цього тумблер існував лише
 * для миші: клавіатура й читалка його не бачили взагалі.
 *
 * ⚠ Напис — ЛІТЕРАЛ, не `t()`, з тієї ж причини, що й `CATALOG_LOAD_FAILED`
 * вище: рядки цього застосунку йдуть із серверного каталогу
 * (`09-seed.sql`), а цей файл — DDL/сід, виключно оркестраторський. Ключа
 * під цей напис там ще немає; голий `t()` без рядка в каталозі показав би
 * читалці позначений ключ (`⟦...⟧`) замість опису кнопки.
 */
const passwordToggleProps = { 'aria-label': 'Toggle password visibility', tabIndex: 0 } as const;

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

  // Перемальовує сторінку, коли каталог доїхав (інакше видно самі ключі).
  useCatalog();

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

  // ⛔ Доки каталог не розв'язано, тексту НЕМАЄ. Перший кадр із позначеними
  // ключами (`⟦login.title⟧`) бачив би кожен користувач при кожному відкритті
  // сторінки — а це рівно те, чим була `A7-33`, тільки коротше (`D-138`).
  if (!isCatalogResolved(preferredLanguage(), 'public')) {
    return (
      <Center h="100vh">
        <Loader size="sm" />
      </Center>
    );
  }

  // ⛔ Каталог розв'язано, але невдало: показуємо причину, а не форму з
  // голими ключами (`⟦login.title⟧` тощо) замість написів (`D-138`).
  // Раніше «розв'язано порожнім через збій» і «розв'язано успішно» нічим не
  // різнилися для цієї перевірки, і форма малювалася в обох випадках.
  if (isCatalogFailed(preferredLanguage(), 'public')) {
    return (
      <Center h="100vh">
        <Card withBorder w={380} p="lg">
          <ErrorAlert error={CATALOG_LOAD_FAILED} />
        </Card>
      </Center>
    );
  }

  return (
    <Center h="100vh">
      <Card withBorder w={380} p="lg">
        <Title order={3} mb="md">
          {t('login.title')}
        </Title>

        {/*
         * ⛔ Справжня `<form>`, а не набір полів із кнопкою.
         *
         * Тут стояв `<Stack>` із `Button onClick`, і Enter у полі пароля
         * НЕ РОБИВ НІЧОГО: браузер надсилає форму по Enter лише тоді, коли
         * форма є. Рефлекс «набрав пароль — натиснув Enter» є в кожного, і
         * на першому ж екрані системи він упирався в тишу; людині без миші
         * лишалося шукати кнопку табом щоразу (`ФВ-14.16`).
         *
         * ⚠ Знайдено прогоном у справжньому браузері (`A7-49`). Жоден
         * компонентний тест цього не бачить: вони натискають кнопку
         * напряму, тобто перевіряють обробник, а не спосіб до нього
         * дійти.
         */}
        <form
          onSubmit={(event) => {
            event.preventDefault();
            void submit(
              '/api/v1/login/local',
              { userName: login, password } satisfies LocalLoginRequest,
            );
          }}
        >
          <Stack gap="sm">
            {/* ⚠ `type="button"` обов'язковий: усередині форми кнопка без
                типу — це кнопка НАДСИЛАННЯ, і вхід через Windows
                перехоплював би Enter замість локального. */}
            <Button
              type="button"
              onClick={() => void submit('/api/v1/login/windows')}
              loading={busy}
            >
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
              visibilityToggleButtonProps={passwordToggleProps}
            />

            <Button type="submit" variant="default" loading={busy}>
              {t('login.submit')}
            </Button>

            <ErrorAlert error={error} />

            <Text size="xs" c="dimmed">
              {t('login.hint')}
            </Text>
          </Stack>
        </form>
      </Card>
    </Center>
  );
}
