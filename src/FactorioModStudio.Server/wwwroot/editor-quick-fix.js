// Returns a single editor range. The caller leaves it unsaved for review.
export function quickFixEdit(text, fix) {
  if (fix.pathCaseFix) {
    const { offset, expectedText, replacement } = fix.pathCaseFix;
    if (!Number.isInteger(offset) || offset < 0 || !expectedText ||
        text.slice(offset, offset + expectedText.length) !== expectedText) return null;
    return { start: offset, end: offset + expectedText.length, value: replacement };
  }

  if (fix.localeKeyFix) {
    const { section, key } = fix.localeKeyFix;
    if (!section || !/^[A-Za-z0-9_.-]+$/.test(key)) return null;
    let offset = 0;
    let inSection = false;
    const headers = [];
    for (const line of text.split('\n')) {
      const header = line.trim().match(/^\[([^\]]+)\]$/);
      if (header) {
        inSection = header[1].trim() === section;
        if (inSection) headers.push(offset + line.length);
      } else if (inSection && !/^[;#]/.test(line.trimStart()) && line.includes('=') && line.split('=', 1)[0].trim() === key) {
        return null;
      }
      offset += line.length + 1;
    }
    if (headers.length !== 1) return null;
    const end = headers[0];
    if (end === text.length) return { start: end, end, value: '\n' + key + '=' };
    return { start: end + 1, end: end + 1, value: key + '=\n', caret: end + key.length + 2 };
  }
  return null;
}
