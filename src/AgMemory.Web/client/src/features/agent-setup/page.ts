import { ui } from '../../ui-kit/index.js';

/** Installation help uses fixed bundled links; it never reads or writes private MCP configuration in the browser. */
export function agentSetupPage(): string {
  return `<section class="page-header"><p class="eyebrow">Интеграция</p><h1>Подключение агента</h1><p>Skill AgMemory входит в приложение. Агент может найти инструкции и установить их в Codex или Cursor в рамках вашего запроса.</p></section>
  <section class="feature-grid" aria-label="Skill и подключение AgMemory">
  ${ui.card('<h2>Skill AgMemory · 2.0</h2><p>Рабочий контекст, знания, процедуры, опыт и обратная связь. Полный пакет включает инструкции, справочники и метаданные.</p><div class="button-row"><a class="ui-button" href="/api/agent-setup/skill.zip" download>Скачать skill</a><a class="ui-button ui-button--secondary" href="/agent-setup.md" target="_blank" rel="noreferrer">Инструкция установки</a></div>')}
  ${ui.card('<h2>Обнаружение для агентов</h2><p>Без JavaScript: <a href="/agent-setup.md" target="_blank" rel="noreferrer">agent-setup.md</a>. Машинный <a href="/api/agent-setup" target="_blank" rel="noreferrer">manifest с SHA-256</a> и <code>/.well-known/agmemory</code>. В свежем MCP вызовите <code>memory_capabilities</code>, затем <code>memory_skill</code>. Если список команд устарел, переподключите MCP.</p>')}
  ${ui.card('<h2>Полный цикл памяти</h2><ol><li>Прочитать рабочий контекст и проверить источники.</li><li>Сохранить полезное резюме L2 с учётом совместной работы.</li><li>Зарегистрировать фактическое использование и обратную связь.</li><li>Сохранить Outcome и классифицировать переиспользуемое знание.</li></ol><p>Процедуры и случаи ошибок добавляются по ситуации. L1 остаётся у клиента; тёплый L3 и оценки не подтверждают истинность.</p>')}
  ${ui.card('<h2>Codex и Cursor</h2><p>Установите папку <code>agmemory</code> в каталог skills нужного клиента. Инструкция описывает глобальную и проектную установку, проверку хешей и сохранение существующих настроек. Для подключения нужен отдельный установленный <code>AgMemory.McpServer</code>, а не Web-процесс.</p><p>Хранилище, scope и происхождение клиента задаёт оператор. Секреты не вводятся в браузере. Обнаружение справки само по себе не разрешает менять конфигурацию.</p>')}
  </section>`;
}
