"""MkDocs hook of the documentation site (tmp/scope-docs-site.md).

Adds the pages whose text lives elsewhere in the repository, so it is written once: the home page and Getting started from the
README's own sections, and the packs from their READMEs. Then points every relative link at the page it names in the site, or,
for a repository file the site does not publish (source, fixtures, the engineering design documents), at GitHub.
"""

import posixpath
import re
from pathlib import Path

from mkdocs.structure.files import File

ROOT = Path(__file__).resolve().parents[2]
GITHUB = "https://github.com/mattjcowan/maquettiste/blob/main/"

# Generated page -> (the repository file it comes from, the README section bounds or None for the whole file).
PACKS = sorted(p.parent.name for p in (ROOT / "packs").glob("*/README.md"))
SOURCES = {
    "index.md": ("README.md", None, "## Getting started"),
    "getting-started.md": ("README.md", "## Getting started", "## Documentation"),
    "packs/index.md": ("packs/README.md", None, None),
    **{f"packs/{name}.md": (f"packs/{name}/README.md", None, None) for name in PACKS},
}

# The README's pictures follow the reader's system theme on GitHub; on the site they follow the site's own theme switch.
PICTURE = re.compile(
    r'<picture>\s*<source media="\(prefers-color-scheme: light\)" srcset="([^"]+)">\s*<img src="([^"]+)" alt="([^"]*)">\s*</picture>'
)
LINK = re.compile(r"(!?\[[^\]]*\]\()([^)\s]+)((?:\s+\"[^\"]*\")?\))")


def _section(text, start, end):
    if start:
        text = text[text.index(start):]
        text = "# Getting started" + text[len(start):]
    if end and end in text:
        text = text[: text.index(end)]
    # The README's pointer to this site is for readers on GitHub.
    return "\n".join(line for line in text.split("\n") if "](https://mattjcowan.github.io/maquettiste/)**" not in line)


def on_files(files, config):
    for uri, (source, start, end) in SOURCES.items():
        text = _section((ROOT / source).read_text(encoding="utf-8"), start, end)
        files.append(File.generated(config, uri, content=text))
    # The API page renders the contract, whose schemas are the repository's schemas/v1 two folders up: the site publishes
    # them at schemas/v1 and the contract's copy points one folder up instead (two would leave the site's base path).
    for schema in sorted((ROOT / "schemas" / "v1").glob("*.json")):
        files.append(File.generated(config, f"schemas/v1/{schema.name}", abs_src_path=str(schema)))
    contract = files.get_file_from_path("api/openapi.yaml")
    if contract is not None:
        files.remove(contract)
        text = (ROOT / "docs" / "api" / "openapi.yaml").read_text(encoding="utf-8").replace("'../../schemas/v1/", "'../schemas/v1/")
        files.append(File.generated(config, "api/openapi.yaml", content=text))
    return files


def _origin(page):
    """The repository path the page's text comes from."""
    uri = page.file.src_uri
    return SOURCES[uri][0] if uri in SOURCES else "docs/" + uri


def _site_page(repo_path):
    """The site page (src_uri) a repository path is published as, or None."""
    for uri, (source, start, _) in SOURCES.items():
        if source == repo_path and (start is None or uri == "index.md"):
            return uri
    if repo_path.startswith("docs/") and not repo_path.startswith("docs/engineering/") and not repo_path.startswith("docs/.hooks/"):
        return repo_path[len("docs/"):]
    return None


def _rewrite(target, page):
    if re.match(r"^[a-z]+:", target) or target.startswith("#") or target.startswith("/"):
        return target
    path, _, anchor = target.partition("#")
    origin = _origin(page)
    repo_path = posixpath.normpath(posixpath.join(posixpath.dirname(origin), path))
    site = _site_page(repo_path)
    # A README section split into two pages: a link to the README's own headings stays on the page that holds them.
    if site == "index.md" and anchor and page.file.src_uri == "getting-started.md":
        site = "getting-started.md"
    if site is None:
        return GITHUB + repo_path + (("#" + anchor) if anchor else "")
    here = posixpath.dirname(page.file.src_uri)
    relative = posixpath.relpath(site, here or ".")
    return relative + (("#" + anchor) if anchor else "")


def on_page_markdown(markdown, page, config, files):
    markdown = PICTURE.sub(lambda m: f"![{m.group(3)}]({m.group(1)}#only-light)\n![{m.group(3)}]({m.group(2)}#only-dark)", markdown)
    out, fenced = [], False
    for line in markdown.split("\n"):
        if line.lstrip().startswith("```"):
            fenced = not fenced
        out.append(line if fenced else LINK.sub(lambda m: m.group(1) + _rewrite(m.group(2), page) + m.group(3), line))
    return "\n".join(out)
