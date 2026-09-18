import { Component, type ErrorInfo, type JSX, type ReactNode } from 'react';
import { MantineProvider } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import { RenderErrorScreen } from './RenderErrorScreen';

/**
 * Останній рубіж (`D14-11`): класова межа помилки навколо всього застосунку.
 *
 * ⚠ Не дублює `errorElement` маршрутів, а покриває те, чого той покрити не
 * може ФІЗИЧНО: помилку в `App.tsx` (тема, `QueryClientProvider`,
 * `RouterProvider`) і помилку самого маршрутизатора — усе, що падає ДО того,
 * як з'явиться дерево маршрутів, у якому живе `errorElement`. Без цієї межі
 * така помилка й далі давала б порожній `#root`.
 *
 * ⛔ Власний `MantineProvider` у запасному дереві — не зайвий шар. Помилка
 * могла статися в самому `App.tsx`, тобто провайдера в дереві вже немає, а
 * компоненти `@mantine/core` без нього кидають («MantineProvider was not
 * found in component tree») — тобто екран помилки впав би сам, і користувач
 * знову побачив би білий екран. Тема береться та сама (`theme.ts`), другого
 * `createTheme` в застосунку немає (`ФВ-14.11`).
 *
 * ⚠ Класовий компонент — не стиль, а єдиний спосіб: гака-аналога
 * `componentDidCatch` у React 19 немає.
 */
export class ErrorBoundary extends Component<{ children: ReactNode }, { error: unknown }> {
  constructor(props: { children: ReactNode }) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error: unknown): { error: unknown } {
    return { error };
  }

  /**
   * ⚠ Слід у консолі лишається обов'язково. Перехоплена помилка інакше зникає
   * безслідно — а це рівно той випадок, коли розробникові потрібен стек
   * компонентів, якого в самій помилці немає.
   *
   * ⛔ Відкладеного каналу звітування на сервер тут НЕМАЄ, і це чесно названо,
   * а не вдано: канал, на який посилається `D14-11` («той самий, що возить
   * промахи i18n»), у клієнті — це `console.error` у `shared/i18n/index.ts`,
   * і лише в режимі розробки; серверного приймача клієнтських помилок не
   * існує. Заводити його звідси означало б додати ендпойнт і зміни в
   * `api/client.ts` поза межами цієї підзадачі.
   */
  override componentDidCatch(error: unknown, info: ErrorInfo): void {
    console.error('Помилка рендера перехоплена межею застосунку.', error, info.componentStack);
  }

  override render(): ReactNode {
    if (this.state.error === null || this.state.error === undefined) {
      return this.props.children;
    }

    return this.fallback();
  }

  private fallback(): JSX.Element {
    return (
      <MantineProvider theme={theme} defaultColorScheme="auto">
        <RenderErrorScreen error={this.state.error} />
      </MantineProvider>
    );
  }
}
