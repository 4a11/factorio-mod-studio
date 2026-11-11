// Tests for wwwroot/problems-logic.js: counting, filtering, grouping and copying the validator's issues.
import test from 'node:test';
import assert from 'node:assert/strict';

const { countBySeverity, issueMatches, sortIssues, filterIssues, groupIssues, issuesAsText } =
  await import('../../src/FactorioModStudio.Server/wwwroot/problems-logic.js');

const issue = (severity, code, message, file, line) => ({ severity, code, message, ...(file ? { file } : {}), ...(line ? { line } : {}) });
const sample = [
  issue('warning', 'FMS044', 'Language "de" is missing 3 keys', 'locale/de/a.cfg'),
  issue('error', 'FMS040', 'Malformed locale line', 'locale/fr/a.cfg', 1),
  issue('info', 'FMS012', 'no changelog.txt'),
  issue('error', 'FMS032', 'require("x") does not match', 'control.lua', 12),
  issue('error', 'FMS040', 'Malformed locale line', 'locale/de/a.cfg', 1),
  issue('warning', 'FMS043', 'missing key', 'control.lua', 3),
];

test('countBySeverity counts each severity and ignores the rest', () => {
  assert.deepEqual(countBySeverity([...sample, { severity: 'odd' }, null]), { error: 3, warning: 2, info: 1 });
  assert.deepEqual(countBySeverity(undefined), { error: 0, warning: 0, info: 0 });
});

test('issueMatches looks at code, message and file:line, every word, any case', () => {
  const i = sample[3];
  assert.ok(issueMatches(i, 'fms032'));
  assert.ok(issueMatches(i, 'CONTROL.lua:12'));
  assert.ok(issueMatches(i, 'require control'));
  assert.ok(!issueMatches(i, 'require locale'));
  assert.ok(issueMatches(i, '   '));
});

test('sortIssues: errors first, then code, file and line', () => {
  assert.deepEqual(sortIssues(sample).map((i) => `${i.code} ${i.file ?? ''}`), [
    'FMS032 control.lua', 'FMS040 locale/de/a.cfg', 'FMS040 locale/fr/a.cfg', 'FMS043 control.lua', 'FMS044 locale/de/a.cfg', 'FMS012 ',
  ]);
});

test('filterIssues drops hidden severities and what the text does not match', () => {
  assert.equal(filterIssues(sample, { hidden: new Set(['error']) }).length, 3);
  assert.deepEqual(filterIssues(sample, { text: 'locale/de' }).map((i) => i.code), ['FMS044', 'FMS040']);
});

test('groupIssues by rule: one group per code, worst first', () => {
  const groups = groupIssues(sample, 'rule');
  assert.deepEqual(groups.map((g) => [g.key, g.severity, g.items.length]), [
    ['FMS032', 'error', 1], ['FMS040', 'error', 2], ['FMS043', 'warning', 1], ['FMS044', 'warning', 1], ['FMS012', 'info', 1],
  ]);
});

test('groupIssues by file: worst file first, issues without a file last, rows by line', () => {
  const groups = groupIssues(sample, 'file');
  assert.deepEqual(groups.map((g) => g.key), ['control.lua', 'locale/de/a.cfg', 'locale/fr/a.cfg', '']);
  assert.deepEqual(groups[0].items.map((i) => i.line), [3, 12]);
  assert.equal(groups[0].severity, 'error');
});

test('groupIssues list: one flat sorted group, none when empty', () => {
  const [all] = groupIssues(sample, 'list');
  assert.equal(all.items.length, sample.length);
  assert.equal(all.items[0].code, 'FMS032');
  assert.deepEqual(groupIssues([], 'list'), []);
});

test('issuesAsText: one line per issue with severity, code and place', () => {
  assert.equal(issuesAsText([sample[3], sample[2]]),
    'error FMS032 control.lua:12 require("x") does not match\ninfo FMS012 no changelog.txt');
});
