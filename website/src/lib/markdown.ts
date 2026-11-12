// A small renderer for the Markdown subset the release notes and the site's own texts use: headings, paragraphs,
// nested bullet lists, **bold**, *italic*, `code` and [links](https://...). Pictures are dropped, everything else
// stays plain text.
// The input is escaped first, so the result is safe to put into a page with set:html.

export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/** One line of text with inline formatting. */
export function inline(source: string): string {
  // Code spans are taken out first, so that nothing inside them is read as formatting.
  const codes: string[] = [];
  let text = escapeHtml(source).replace(/`([^`]+)`/g, (_, code: string) => {
    codes.push(`<code>${code}</code>`);
    return `\u0000${codes.length - 1}\u0000`;
  });

  text = text
    // Pictures are left out: the notes link to the release on GitHub, which shows them.
    .replace(/\s*!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" rel="noopener">$1</a>')
    .replace(/\*\*([^*]+?)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^\w*])\*(?!\s)([^*]+?)\*(?![\w*])/g, (match, before: string, body: string) =>
      body.endsWith(' ') ? match : `${before}<em>${body}</em>`,
    );

  return text.replace(/\u0000(\d+)\u0000/g, (_, index: string) => codes[Number(index)] ?? '');
}

interface ListItem {
  depth: number;
  text: string;
}

function renderList(items: ListItem[], depth: number): string {
  let html = '<ul>';
  let i = 0;
  while (i < items.length) {
    const item = items[i]!;
    let end = i + 1;
    while (end < items.length && items[end]!.depth > depth) end++;
    const children = items.slice(i + 1, end);
    html += `<li>${inline(item.text)}${children.length > 0 ? renderList(children, depth + 1) : ''}</li>`;
    i = end;
  }
  return `${html}</ul>`;
}

/**
 * Blocks of Markdown as HTML. `headingOffset` moves the headings down: with 1 a "## Title" becomes <h3>.
 */
export function renderMarkdown(source: string, headingOffset = 0): string {
  const lines = source.replace(/\r\n?/g, '\n').split('\n');
  const out: string[] = [];
  let paragraph: string[] = [];
  let list: ListItem[] = [];

  const flushParagraph = () => {
    if (paragraph.length > 0) out.push(`<p>${inline(paragraph.join(' '))}</p>`);
    paragraph = [];
  };
  const flushList = () => {
    if (list.length > 0) {
      // Depths are normalised so that a list never starts deeper than its first item.
      const base = Math.min(...list.map((item) => item.depth));
      out.push(
        renderList(
          list.map((item) => ({ ...item, depth: item.depth - base })),
          0,
        ),
      );
    }
    list = [];
  };

  for (let n = 0; n < lines.length; n++) {
    const line = lines[n]!;
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    const bullet = /^(\s*)[-*]\s+(.*)$/.exec(line);

    if (line.trim() === '') {
      flushParagraph();
      // A blank line between two bullets does not end the list.
      const next = lines.slice(n + 1).find((l) => l.trim() !== '');
      if (!next || !/^\s*[-*]\s+/.test(next)) flushList();
    } else if (heading) {
      flushParagraph();
      flushList();
      const level = Math.min(6, heading[1]!.length + headingOffset);
      out.push(`<h${level}>${inline(heading[2]!.trim())}</h${level}>`);
    } else if (bullet) {
      flushParagraph();
      list.push({ depth: Math.floor(bullet[1]!.length / 2), text: bullet[2]!.trim() });
    } else if (list.length > 0) {
      // A wrapped line continues the bullet above it.
      list[list.length - 1]!.text += ` ${line.trim()}`;
    } else {
      paragraph.push(line.trim());
    }
  }
  flushParagraph();
  flushList();
  return out.join('\n');
}
