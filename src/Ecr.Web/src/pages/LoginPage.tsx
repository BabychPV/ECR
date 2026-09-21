import { useState, type JSX } from 'react';
import {
  Alert,
  Box,
  Button,
  Card,
  Center,
  Divider,
  Group,
  Loader,
  NativeSelect,
  PasswordInput,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { BrandMark } from '@/shared/ui/BrandMark';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { apiFetch, EcrApiError, LOGIN_REASON_PARAM } from '@/api/client';
import type { CurrentUserDto, LocalLoginRequest } from '@/api/types';
import { anyLostEdits, takeLostEdits, type LostEdits } from '@/features/grid/lostEdits';
import { safeReturnPath } from './safeReturnPath';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import {
  isCatalogFailed,
  isCatalogResolved,
  loadCatalog,
  preferredLanguage,
  setLanguage,
  t,
} from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';
import { usePublicBootstrap } from '@/features/public/api';
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
 * Підпис перемикача мови на екрані входу (`BE-07`).
 *
 * ⛔ ЛІТЕРАЛ, а не `t('profile.language')`, і причина конкретна, а не
 * «ще один виняток». Цей ключ заведений у `09-seed.sql` з областю **1
 * (private)**, тобто в публічний зріз каталогу він не потрапляє за
 * визначенням (`D-114`): на екрані входу `t()` повернув би позначений ключ
 * `⟦profile.language⟧` — рівно те, від чого рятує `D-138`. Завести окремий
 * публічний ключ — один рядок сіду, і він СВІДОМО відкладений: `09-seed.sql`
 * зараз змінює інша гілка (#368), а два записи в той самий `MERGE` дають
 * конфлікт заради підпису, якого ніхто не бачить (перемикач має видиму
 * назву мови в кожному пункті).
 *
 * ⚠ Тому підпис лише для читалки: `aria-label`, без видимого тексту.
 */
const LANGUAGE_LABEL = 'Interface language';

/*
 * Повідомлення про втрачені правки (`features/grid/lostEdits.ts`).
 *
 * ⚠ Ключі `login.lostEdits.title`, `login.lostEdits.text` ({count},
 * {documentId}), `login.lostEdits.continue` — область public; рядки сіду
 * заводить інтегратор.
 */

/** Слід саме цього користувача — `userId` з профілю щойно відкритої сесії. */
async function ownLostEdits(): Promise<LostEdits | null> {
  try {
    const me = await apiFetch<CurrentUserDto>('/api/v1/me');
    return takeLostEdits(me.userId);
  } catch {
    return null;
  }
}

/**
 * Пояснення, чому людину повернули на вхід після обриву сесії.
 *
 * ⚠ Ключ `login.sessionInvalidated` має бути в ПУБЛІЧНОМУ зрізі сіду (0):
 * рядок заводить інтегратор; до того сторож каталогу червоний — очікувано.
 */
function sessionInvalidatedText(): string {
  return t('login.sessionInvalidated');
}

/**
 * Вхід: доменний і локальний.
 *
 * ⚠ Обидва способи видають **ту саму cookie** і той самий профіль. Різні
 * механізми сесії для двох способів входу означали б, що половина перевірок
 * безпеки працює лише для однієї половини користувачів.
 */
export function LoginPage(): JSX.Element {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const sessionInvalidated = searchParams.get(LOGIN_REASON_PARAM) === 'session-invalidated';
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [lost, setLost] = useState<LostEdits | null>(null);

  // Перемальовує сторінку, коли каталог доїхав (інакше видно самі ключі).
  useCatalog();

  // ⚠ Викликається ДО ранніх повернень нижче (завантаження каталогу, збій
  // каталогу): порядок хуків у React має бути однаковий на кожному рендері,
  // і хук після `if (…) return` — це помилка, яка проявляється лише в момент,
  // коли гілка змінюється.
  const bootstrap = usePublicBootstrap();

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

      // ⚠ Слід втрачених правок читається лише ПІСЛЯ входу і лише свого
      // користувача: до входу невідомо, чий він, а показати його будь-кому
      // означало б розкрити чужу роботу на спільному комп'ютері.
      const found = anyLostEdits() ? await ownLostEdits() : null;
      if (found !== null) {
        setLost(found);
        return;
      }

      // Повернення туди, де людина була, — лише на внутрішній шлях.
      navigate(safeReturnPath(searchParams.get('from')), { replace: true });
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

  if (lost !== null) {
    return (
      <Center h="100vh">
        <Card withBorder w={380} p="lg">
          <Stack gap="sm">
            <Alert color="statusError" role="alert" title={t('login.lostEdits.title')}>
              {t('login.lostEdits.text', { count: lost.count, documentId: lost.documentId })}
            </Alert>
            <Button onClick={() => navigate(safeReturnPath(lost.from), { replace: true })}>
              {t('login.lostEdits.continue')}
            </Button>
          </Stack>
        </Card>
      </Center>
    );
  }

  return (
    <Center h="100vh">
      <Card withBorder w={380} p="lg">
        {/*
         * ⚠ Знак і скорочення — ОЗДОБА, повна назва — заголовок. Порядок саме
         * такий: зчитувач екрана має прочитати назву системи один раз і як
         * заголовок, а не як три уривки тексту (той самий прийом, що в шапці
         * `AppLayout`).
         *
         * ⛔ До цього тут стояв самий лише `Title order={3}` з повною назвою:
         * три слова жирним на всю ширину картки, які переносилися на два
         * рядки і були найгучнішим елементом екрана. Перший екран системи не
         * мав ані знака, ані впізнаваності — лише довгий рядок.
         */}
        <Stack gap="xs" align="center" mb="lg">
          {/* ⚠ Знак і слово мають ОДНАКОВУ пару відтінків. Спершу колір стояв
              один на всю групу — і в темній темі знак лишався темно-синім на
              темному тлі, тоді як слово світлішало. Видно це було лише на
              знімку темної теми, не з коду. */}
          <Group gap="xs" wrap="nowrap">
            <Box c="brand.8" darkHidden>
              <BrandMark size={30} />
            </Box>
            <Box c="brand.2" lightHidden>
              <BrandMark size={30} />
            </Box>
            <Text component="span" fz={30} fw={700} lh={1} c="brand.8" darkHidden>
              ECR
            </Text>
            <Text component="span" fz={30} fw={700} lh={1} c="brand.2" lightHidden>
              ECR
            </Text>
          </Group>

          {/* ⚠ Заголовок лишається `h3` — e2e й перевірки доступності шукають
              саме роль, а не розмір. Змінюється вага й вирівнювання, не роль. */}
          <Title order={3} fz="sm" fw={500} ta="center">
            {t('login.title')}
          </Title>
        </Stack>

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
            {/*
              * Сервер обірвав чинну сесію штампом безпеки (`401 ECR-AUTH-0401`
              * з тілом) — пояснюємо, чому людину повернули. Звичайний «не
              * входив» причини не несе й банера не має.
              */}
            {sessionInvalidated && (
              <Alert color="blue" variant="light" data-login-reason="session-invalidated">
                {sessionInvalidatedText()}
              </Alert>
            )}

            {/*
              * ⛔ Кнопка доменного входу малюється лише тоді, коли схема
              * Negotiate СПРАВДІ зареєстрована на сервері
              * (`GET /api/v1/public/bootstrap`). Доти вона стояла завжди, і на
              * майданчику з `Auth:EnableNegotiate=false` єдиним способом
              * дізнатися, що доменний вхід вимкнено, було натиснути її й
              * отримати 401. Кнопка, яка гарантовано відмовляє, гірша за її
              * відсутність: вона виглядає як дефект продукту.
              *
              * ⚠ `type="button"` обов'язковий: усередині форми кнопка без
              * типу — це кнопка НАДСИЛАННЯ, і вхід через Windows
              * перехоплював би Enter замість локального.
              */}
            {bootstrap.windowsSignInEnabled && (
              <Button
                type="button"
                onClick={() => void submit('/api/v1/login/windows')}
                loading={busy}
              >
                {t('login.windows')}
              </Button>
            )}

            {bootstrap.windowsSignInEnabled && bootstrap.localSignInEnabled && (
              <Divider label={t('login.or')} labelPosition="center" />
            )}

            {bootstrap.localSignInEnabled && (
              <>
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
              </>
            )}

            <ErrorAlert error={error} />

            <Text size="xs" c="dimmed">
              {t('login.hint')}
            </Text>

            {/*
              * ⛔ Перелік мов — із реєстру сервера, не константа бандла
              * (`ФВ-14.9`): «додавання мови — запис у реєстр, не збірка
              * клієнта». До входу його віддає анонімний `bootstrap`, бо
              * `GET /api/v1/languages` вимагає автентифікації — тобто до цієї
              * дії обіцянка трималася лише ПІСЛЯ входу, а перший екран
              * системи вгадував мову з браузера і не давав її змінити.
              *
              * ⚠ Ховається на одній мові: вибір з одного пункту не є вибором.
              */}
            {bootstrap.languages.length > 1 && (
              <NativeSelect
                size="xs"
                variant="unstyled"
                aria-label={LANGUAGE_LABEL}
                value={preferredLanguage()}
                data={bootstrap.languages.map((item) => ({
                  value: item.code,
                  label: item.nameNative,
                }))}
                onChange={(event) => {
                  const value = event.currentTarget.value;
                  if (value === preferredLanguage()) return;

                  setLanguage(value);

                  // ⚠ Область `public`, а не `private`, як у перемикачі
                  // всередині застосунку: приватний зріз анонімний запит не
                  // отримає (ФВ-14.2), і сторінка лишилася б із позначеними
                  // ключами замість написів.
                  void loadCatalog(value, 'public');
                }}
              />
            )}

            {/*
              * ⚠ Версія — рядок вигляду `1.0.0` і тільки: метадані збірки
              * (хеш коміту, гілка) сервер зрізає ДО відповіді, бо цей екран
              * читає кожен, хто дістався порту. Порожнє значення означає «не
              * знаємо» і не малюється зовсім.
              */}
            {bootstrap.productVersion.length > 0 && (
              <Text size="xs" c="dimmed" ta="center">
                {bootstrap.productVersion}
              </Text>
            )}
          </Stack>
        </form>
      </Card>
    </Center>
  );
}
