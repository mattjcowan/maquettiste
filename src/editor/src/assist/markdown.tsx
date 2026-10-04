// A small, safe Markdown renderer for the assistant's answers: the text is parsed into blocks and inline spans and drawn
// as React elements (never as HTML), so nothing the model writes can inject markup. It covers what answers use: headings,
// paragraphs, bullet and numbered lists, fenced code, inline code, bold, italics and links (http, https and same-site
// paths only).
import type { ReactNode } from "react";

export type Inline =
  | { type: "text"; text: string }
  | { type: "code"; text: string }
  | { type: "strong"; children: Inline[] }
  | { type: "em"; children: Inline[] }
  | { type: "link"; href: string; children: Inline[] };

export type Block =
  | { type: "heading"; level: 1 | 2 | 3; children: Inline[] }
  | { type: "paragraph"; children: Inline[] }
  | { type: "list"; ordered: boolean; items: Inline[][] }
  | { type: "code"; language: string; text: string };

const SAFE_HREF = /^(https?:\/\/|\/(?!\/))/i;

/** Inline spans of one line or paragraph. */
export function parseInline(text: string): Inline[] {
  const out: Inline[] = [];
  let plain = "";
  const flush = () => {
    if (plain) out.push({ type: "text", text: plain });
    plain = "";
  };
  let i = 0;
  while (i < text.length) {
    const rest = text.slice(i);
    let m: RegExpExecArray | null;
    if ((m = /^`([^`]+)`/.exec(rest))) {
      flush();
      out.push({ type: "code", text: m[1] });
      i += m[0].length;
    } else if ((m = /^\*\*([^*]+?)\*\*/.exec(rest)) || (m = /^__([^_]+?)__/.exec(rest))) {
      flush();
      out.push({ type: "strong", children: parseInline(m[1]) });
      i += m[0].length;
    } else if ((m = /^\*([^*\s][^*]*?)\*/.exec(rest)) || (m = /^_([^_\s][^_]*?)_(?![A-Za-z0-9])/.exec(rest))) {
      flush();
      out.push({ type: "em", children: parseInline(m[1]) });
      i += m[0].length;
    } else if ((m = /^\[([^\]]+)\]\(([^)\s]+)\)/.exec(rest))) {
      flush();
      if (SAFE_HREF.test(m[2])) out.push({ type: "link", href: m[2], children: parseInline(m[1]) });
      else out.push(...parseInline(m[1]));
      i += m[0].length;
    } else {
      plain += text[i];
      i++;
    }
  }
  flush();
  return out;
}

/** The blocks of a Markdown text. */
export function parseMarkdown(text: string): Block[] {
  const lines = text.replace(/\r\n?/g, "\n").split("\n");
  const blocks: Block[] = [];
  let paragraph: string[] = [];
  let list: { ordered: boolean; items: string[] } | null = null;
  const endParagraph = () => {
    if (paragraph.length) blocks.push({ type: "paragraph", children: parseInline(paragraph.join(" ")) });
    paragraph = [];
  };
  const endList = () => {
    if (list) blocks.push({ type: "list", ordered: list.ordered, items: list.items.map(parseInline) });
    list = null;
  };
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const fence = /^\s*```\s*([\w+-]*)\s*$/.exec(line);
    if (fence) {
      endParagraph();
      endList();
      const body: string[] = [];
      i++;
      while (i < lines.length && !/^\s*```\s*$/.test(lines[i])) body.push(lines[i++]);
      blocks.push({ type: "code", language: fence[1], text: body.join("\n") });
      continue;
    }
    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      endParagraph();
      endList();
      blocks.push({ type: "heading", level: Math.min(3, heading[1].length) as 1 | 2 | 3, children: parseInline(heading[2].trim()) });
      continue;
    }
    const bullet = /^\s*[-*+]\s+(.*)$/.exec(line);
    const numbered = /^\s*\d+[.)]\s+(.*)$/.exec(line);
    if (bullet || numbered) {
      endParagraph();
      const ordered = !bullet;
      const current = list as { ordered: boolean; items: string[] } | null;
      if (current && current.ordered !== ordered) endList();
      list ??= { ordered, items: [] };
      list.items.push((bullet ?? numbered)![1]);
      continue;
    }
    if (line.trim() === "") {
      endParagraph();
      endList();
      continue;
    }
    if (list && /^\s{2,}\S/.test(line)) {
      const items = (list as { items: string[] }).items;
      items[items.length - 1] += ` ${line.trim()}`;
      continue;
    }
    endList();
    paragraph.push(line.trim());
  }
  endParagraph();
  endList();
  return blocks;
}

function renderInline(spans: Inline[]): ReactNode[] {
  return spans.map((s, i) => {
    switch (s.type) {
      case "text":
        return s.text;
      case "code":
        return (
          <code key={i} className="rounded-[3px] bg-app px-1 font-mono text-[12px]">
            {s.text}
          </code>
        );
      case "strong":
        return <strong key={i}>{renderInline(s.children)}</strong>;
      case "em":
        return <em key={i}>{renderInline(s.children)}</em>;
      case "link":
        return (
          <a
            key={i}
            href={s.href}
            className="text-accent underline-offset-2 hover:underline"
            target={/^https?:/i.test(s.href) ? "_blank" : undefined}
            rel="noreferrer noopener"
          >
            {renderInline(s.children)}
          </a>
        );
    }
  });
}

export function Markdown({ text }: { text: string }) {
  const blocks = parseMarkdown(text);
  return (
    <div className="flex flex-col gap-1.5 text-13 leading-5" data-testid="assistant-markdown">
      {blocks.map((b, i) => {
        switch (b.type) {
          case "heading":
            return b.level === 1 ? (
              <h3 key={i} className="text-14 font-semibold">
                {renderInline(b.children)}
              </h3>
            ) : (
              <h4 key={i} className="text-13 font-semibold">
                {renderInline(b.children)}
              </h4>
            );
          case "paragraph":
            return (
              <p key={i} className="whitespace-pre-wrap break-words">
                {renderInline(b.children)}
              </p>
            );
          case "list": {
            const items = b.items.map((item, j) => (
              <li key={j} className="break-words">
                {renderInline(item)}
              </li>
            ));
            return b.ordered ? (
              <ol key={i} className="ml-5 list-decimal">
                {items}
              </ol>
            ) : (
              <ul key={i} className="ml-5 list-disc">
                {items}
              </ul>
            );
          }
          case "code":
            return (
              <pre key={i} className="overflow-x-auto rounded-control border border-default bg-app p-1.5 font-mono text-[12px] leading-4">
                <code>{b.text}</code>
              </pre>
            );
        }
      })}
    </div>
  );
}
