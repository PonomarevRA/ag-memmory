import { escapeHtml } from '../shared/html.js';

/** Internal string-view primitives. They have no route and are reused by feature views. */
export const ui = {
  escape: escapeHtml,
  button: (label: string, options: { id?: string; secondary?: boolean; type?: 'button' | 'submit' } = {}) =>
    `<button${options.id ? ` id="${escapeHtml(options.id)}"` : ''} class="ui-button${options.secondary ? ' ui-button--secondary' : ''}" type="${options.type ?? 'button'}">${escapeHtml(label)}</button>`,
  card: (content: string, className = '') => `<section class="ui-card${className ? ` ${className}` : ''}">${content}</section>`,
  container: (content: string, className: string) => `<section class="${escapeHtml(className)}">${content}</section>`,
  input: (id: string, value: string, placeholder: string) => `<input id="${escapeHtml(id)}" class="ui-input" maxlength="120" value="${escapeHtml(value)}" placeholder="${escapeHtml(placeholder)}">`,
  link: (href: string, label: string, className = '') => `<a class="${escapeHtml(className)}" href="${escapeHtml(href)}">${escapeHtml(label)}</a>`,
  table: (head: string, body: string, className: string) => `<table class="${escapeHtml(className)}"><thead>${head}</thead><tbody>${body}</tbody></table>`,
  chip: (label: string, active = false) => `<span class="memory-reader-facet${active ? ' is-active' : ''}">${escapeHtml(label)}</span>`,
  status: (message: string, level: 'warning' | 'error' | 'success' = 'warning') =>
    `<p class="status-banner status-banner--${level}"><span class="status-banner__icon" aria-hidden="true">!</span><span>${escapeHtml(message)}</span></p>`
};
