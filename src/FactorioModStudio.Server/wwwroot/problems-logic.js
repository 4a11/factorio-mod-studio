// Pure helpers of the Problems tab: counting, filtering, grouping and copying the validator's issues. No DOM, no i18n:
// problems-view.js turns the result into rows. An issue is { severity, code, message, file?, line? } as the overview API sends it.

export const SEVERITIES = ['error', 'warning', 'info'];
export const GROUPINGS = ['rule', 'file', 'list'];
const RANK = { error: 0, warning: 1, info: 2 };
const rank = (severity) => RANK[severity] ?? 3;

const fileOf = (issue) => (typeof issue?.file === 'string' ? issue.file : '');
const lineOf = (issue) => (Number.isInteger(issue?.line) && issue.line > 0 ? issue.line : 0);

/** { error, warning, info } counts. */
export function countBySeverity(issues) {
  const counts = { error: 0, warning: 0, info: 0 };
  for (const issue of issues ?? []) if (issue && issue.severity in counts) counts[issue.severity]++;
  return counts;
}

/** Case-insensitive: every word of `text` must occur in the code, the message or the file (with its line, "data.lua:12"). */
export function issueMatches(issue, text) {
  const words = String(text ?? '').toLowerCase().split(/\s+/).filter(Boolean);
  if (!words.length) return true;
  const line = lineOf(issue);
  const haystack = `${issue?.code ?? ''}\n${issue?.message ?? ''}\n${fileOf(issue)}${line ? `:${line}` : ''}`.toLowerCase();
  return words.every((word) => haystack.includes(word));
}

/** Severity first, then code, file and line; the original order breaks ties. */
export function sortIssues(issues) {
  return (issues ?? [])
    .map((issue, index) => ({ issue, index }))
    .sort((a, b) => rank(a.issue.severity) - rank(b.issue.severity)
      || String(a.issue.code ?? '').localeCompare(String(b.issue.code ?? ''))
      || fileOf(a.issue).localeCompare(fileOf(b.issue))
      || lineOf(a.issue) - lineOf(b.issue)
      || a.index - b.index)
    .map((x) => x.issue);
}

/** The issues that pass the text filter and whose severity is not in `hidden` (a Set). */
export function filterIssues(issues, { text = '', hidden = new Set() } = {}) {
  return (issues ?? []).filter((issue) => issue && !hidden.has(issue.severity) && issueMatches(issue, text));
}

/**
 * Groups for the view: by rule (code), by file ('' = issues that name no file) or one flat group ('list').
 * Each group is { key, severity (the worst in it), items (sorted) }; groups are ordered worst severity first, then by key
 * (in 'file' mode the issues without a file come last).
 */
export function groupIssues(issues, grouping) {
  const sorted = sortIssues(issues);
  if (grouping === 'list') return sorted.length ? [{ key: '', severity: sorted[0].severity, items: sorted }] : [];
  const byKey = new Map();
  for (const issue of sorted) {
    const key = grouping === 'file' ? fileOf(issue) : String(issue.code ?? '');
    if (!byKey.has(key)) byKey.set(key, []);
    byKey.get(key).push(issue);
  }
  const groups = [...byKey].map(([key, items]) => ({ key, severity: items.reduce((w, i) => (rank(i.severity) < rank(w) ? i.severity : w), items[0].severity), items }));
  if (grouping === 'file') {
    // Within a file the order that reads best is the file's own: by line.
    for (const g of groups) g.items = [...g.items].sort((a, b) => lineOf(a) - lineOf(b) || rank(a.severity) - rank(b.severity));
    return groups.sort((a, b) => (a.key === '') - (b.key === '') || rank(a.severity) - rank(b.severity) || a.key.localeCompare(b.key));
  }
  return groups.sort((a, b) => rank(a.severity) - rank(b.severity) || a.key.localeCompare(b.key));
}

/** One line per issue, for pasting into a bug report: "error FMS032 graphics/x.lua:11 require(...) does not match ...". */
export function issuesAsText(issues) {
  return sortIssues(issues).map((issue) => {
    const line = lineOf(issue);
    const where = fileOf(issue) ? ` ${fileOf(issue)}${line ? `:${line}` : ''}` : '';
    return `${issue.severity} ${issue.code ?? ''}${where} ${issue.message ?? ''}`.replace(/\s+/g, ' ').trim();
  }).join('\n');
}
