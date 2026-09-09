import { useEffect, useState, type JSX, type ReactNode } from 'react';
import {
  Badge,
  Button,
  Card,
  Checkbox,
  Divider,
  Group,
  NumberInput,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  useMantineColorScheme,
} from '@mantine/core';
import { EcrApiError } from '@/api/client';
import { cellStateClass, type CellStateName } from '@/features/grid/cellState';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { applyDensity, density, setDensity, type Density } from '@/shared/theme/preferences';
import { cellState } from '@/shared/theme/theme';
import { loadCatalog, preferredLanguage } from '@/shared/i18n';
import { useCatalog } from '@/shared/i18n/useCatalog';

/**
 * Каталог компонентів (`ЕТАП 7`, модуль 7.8).
 *
 * ⛔ Це **не** Storybook і не документація. Це один екран, який рендерить усе
 * поруч, і сенс рівно в цьому: п'ять відтінків сірого, що розповзлися по
 * п'ятнадцяти областях, видно **лише поруч**. Коли компоненти на різних
 * екранах, розбіжність не помітна нікому, включно з тим, хто її створив.
 *
 * ⚠ Сторінка також є єдиним місцем, де перевіряються стани комірок **у
 * градаціях сірого**: перемикач знеколірення нижче застосовує фільтр до
 * зразків. Якщо стани при цьому зливаються — `ФВ-14.18` не виконана, хоч би
 * що казав `axe-core`.
 *
 * ⛔ Маршрут доступний **лише в режимі розробки** (`import.meta.env.DEV`) і
 * поза `AppLayout`: інакше він потребував би входу, тобто був би недоступний
 * саме тоді, коли потрібен — при налаштуванні вигляду.
 */
export function KitchenSinkPage(): JSX.Element {
  const { colorScheme, setColorScheme } = useMantineColorScheme();
  const [rows, setRows] = useState<Density>(density);
  const [grey, setGrey] = useState(false);

  // ⚠ Каталог тягнеться і тут: сторінка живе поза AppLayout, тобто рядків їй
  // ніхто не завантажить. Без цього зразок стану помилки показував би
  // state.errorTitle — тобто демонстрував би дефект A7-33 замість вимоги.
  useCatalog();
  useEffect(() => {
    void loadCatalog(preferredLanguage(), 'public');
  }, []);

  function changeDensity(value: Density): void {
    setRows(value);
    setDensity(value);
    applyDensity(value);
  }

  return (
    <Stack gap="lg" p="md" style={grey ? { filter: 'grayscale(1)' } : undefined}>
      <PageHeader title="Каталог компонентів" />

      <Group gap="lg" align="flex-end">
        <div>
          <Text size="xs" c="dimmed" mb="xs">
            Тема
          </Text>
          <SegmentedControl
            size="xs"
            value={colorScheme}
            onChange={(v) => setColorScheme(v as 'light' | 'dark' | 'auto')}
            data={[
              { value: 'auto', label: 'Системна' },
              { value: 'light', label: 'Світла' },
              { value: 'dark', label: 'Темна' },
            ]}
          />
        </div>

        <div>
          <Text size="xs" c="dimmed" mb="xs">
            Щільність
          </Text>
          <SegmentedControl
            size="xs"
            value={rows}
            onChange={(v) => changeDensity(v as Density)}
            data={[
              { value: 'compact', label: 'Щільна' },
              { value: 'comfortable', label: 'Вільна' },
            ]}
          />
        </div>

        {/*
         * ⛔ Головна перевірка сторінки. Знеколірення — це не демонстрація, а
         * єдиний спосіб довести, що колір не є єдиним носієм: при `grayscale(1)`
         * від кольору не лишається нічого, і розрізняти стани мусить сама форма.
         */}
        <Switch
          checked={grey}
          onChange={(event) => setGrey(event.currentTarget.checked)}
          label="Градації сірого"
        />
      </Group>

      <Divider />

      {/*
       * ⛔ Стенд для ВИМІРЮВАННЯ, не для показу (`D-140`). Три умови, без яких
       * піксельний вимір бреше:
       *   1. шість комірок — п'ять станів плюс звичайна;
       *   2. ОДНАКОВИЙ вміст у всіх шести, інакше різниця вимірює текст;
       *   3. однакова геометрія, фіксований розмір.
       *
       * ⚠ Він не сховається за `display: none`: прихований елемент не
       * рендериться, і знімати з нього нема чого. Тому просто малий і зверху.
       */}
      <div data-measure="cell-states">
        {[...(Object.keys(cellState) as CellStateName[]), null].map((name) => (
          <div
            key={name ?? 'normal'}
            data-measure-cell={name ?? 'normal'}
            className={name === null ? '' : cellStateClass(name)}
            style={{ width: 100, height: 24, boxSizing: 'border-box', overflow: 'hidden' }}
          >
            1 234,56
          </div>
        ))}
      </div>

      <Section title="Стани комірки (ФВ-14.18, D-128)">
        <Text size="sm" c="dimmed">
          П'ять станів. Увімкніть «градації сірого»: якщо стани перестали
          розрізнятися — вимога не виконана.
        </Text>

        <Table withTableBorder withColumnBorders className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Стан</Table.Th>
              <Table.Th>Зразок</Table.Th>
              <Table.Th>Другий носій</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(Object.keys(cellState) as CellStateName[]).map((name) => (
              <Table.Tr key={name}>
                <Table.Td>
                  <Text size="sm" ff="monospace">
                    {name}
                  </Text>
                </Table.Td>
                <Table.Td
                  className={cellStateClass(name)}
                  data-cell-state={name}
                  style={{ minWidth: 120 }}
                >
                  1 234,56
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{cellState[name].marker}</Text>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Section>

      <Section title="Стани подання (ФВ-14.21…14.25)">
        <Text size="sm" c="dimmed">
          Чотири стани поруч. Порожньо і помилка не мають права виглядати
          однаково (`ФВ-14.22`, `A7-04`).
        </Text>

        <Group align="stretch" grow>
          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Завантаження
            </Text>
            <AsyncBoundary isPending error={null} data={undefined} skeleton="table">
              {() => <div />}
            </AsyncBoundary>
          </Card>

          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Порожньо
            </Text>
            <AsyncBoundary<string[]>
              isPending={false}
              error={null}
              data={[]}
              isEmpty={(d) => d.length === 0}
              emptyTitle="У цьому проєкті ще немає документів"
              emptyHint="Документи з'являються після відкриття періоду."
              emptyAction={<Button size="xs">Створити документ</Button>}
            >
              {() => <div />}
            </AsyncBoundary>
          </Card>
        </Group>

        <Group align="stretch" grow>
          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Помилка
            </Text>
            <AsyncBoundary
              isPending={false}
              error={
                new EcrApiError({
                  title: 'Період закрито',
                  detail: 'Період закрито: зміни потребують окремого погодження.',
                  status: 409,
                  // ⚠ Плейсхолдер навмисно НЕ з каталогу. Тут стояв
                  // `ECR-PER-0409` — родини `PER` не існує, код вигадали для
                  // показу. Формат `ECR-<ДОМЕН>-<HTTP>` — це маршрут: за
                  // родиною клієнт вирішує, у який обробник віддати відмову,
                  // тож вигаданий код у прикладі стає зразком, який копіюють.
                  //
                  // `HTTP-409` — форма, яку `client.ts` породжує сам у
                  // `problemOf`, коли відповідь не є `problem+json`. Вона
                  // очевидно не з каталогу, і показувати їй є що: сторінка
                  // демонструє ВИГЛЯД компонента, а не сценарій.
                  errorCode: 'HTTP-409',
                  correlationId: 'cid-demo-1',
                })
              }
              data={undefined}
              onRetry={() => {}}
            >
              {() => <div />}
            </AsyncBoundary>
          </Card>

          <Card withBorder>
            <Text size="xs" c="dimmed" mb="xs">
              Дані
            </Text>
            <AsyncBoundary<string[]>
              isPending={false}
              error={null}
              data={['ECR-2026-001', 'ECR-2026-002']}
              isEmpty={(d) => d.length === 0}
            >
              {(d) => (
                <Stack gap="xs">
                  {d.map((key) => (
                    <Text key={key} size="sm">
                      {key}
                    </Text>
                  ))}
                </Stack>
              )}
            </AsyncBoundary>
          </Card>
        </Group>
      </Section>

      <Section title="Керування">
        <Group align="flex-end">
          <Button>Основна</Button>
          <Button variant="default">Звичайна</Button>
          <Button variant="subtle">Тиха</Button>
          <Button color="statusError">Небезпечна</Button>
          <Button loading>Триває</Button>
          <Button disabled>Недоступна</Button>
        </Group>

        <Group align="flex-end">
          <TextInput label="Текст" placeholder="—" />
          <NumberInput label="Число" decimalScale={2} />
          <Select label="Вибір" data={['Перше', 'Друге']} />
          <Checkbox label="Прапорець" />
        </Group>

        <Group>
          <Badge>Звичайний</Badge>
          <Badge color="statusWarning" variant="filled">
            Симуляція
          </Badge>
          <Badge color="statusError">Помилка</Badge>
        </Group>
      </Section>

      <Section title="Типографіка (ФВ-14.13)">
        <Title order={2}>Заголовок h2</Title>
        <Title order={3}>Заголовок h3</Title>
        <Text size="md">md 15px — форми</Text>
        <Text size="sm">sm 13px — основний</Text>
        <Text size="xs">xs 11px — щільні таблиці</Text>
      </Section>

      <Section title="Довгий рядок (ФВ-14.30)">
        <Text size="sm" c="dimmed">
          Казахська й російська на 20–40 % довші за англійську; розмітка не має
          ламатися.
        </Text>

        <Group grow maw={420}>
          <Button>
            <span className="ecr-ellipsis">
              Қоршаған ортаны қорғау жөніндегі есептілікті бекіту
            </span>
          </Button>
        </Group>
      </Section>
    </Stack>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }): JSX.Element {
  return (
    <Stack gap="sm">
      <Title order={4}>{title}</Title>
      {children}
    </Stack>
  );
}
