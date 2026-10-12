#!/usr/bin/env python3
"""Turns a Markdown report (session-report.py, match-report.py) into a shareable PDF, printed by Edge or Chrome in
headless mode from an HTML page that is deleted afterwards (kept with --keep-html, or the only output with
--no-pdf: a self-contained file that opens in any browser).

The PDF is A4 landscape with compact tables, so wide tables (the player table has 17 columns) fit the page; table
headers wrap instead of widening the table.

Usage:
  python scripts/render-report.py reports/2026-10-12.md               # writes reports/2026-10-12.pdf
  python scripts/render-report.py reports/2026-10-12.md --keep-html   # and keeps reports/2026-10-12.html
  python scripts/render-report.py reports/2026-10-12.md --no-pdf      # HTML only
  python scripts/render-report.py changelog.md --portrait             # A4 portrait, for narrow documents

Needs the Python package "markdown" (pip install markdown). The PDF needs Edge or Chrome; set BROWSER to its path
if it isn't found.
"""
import html
import os
import shutil
import subprocess
import sys
import tempfile

import markdown

CSS = """
@page { size: A4 landscape; margin: 10mm; }
body { font-family: "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif; color: #1d232a; margin: 0 auto;
       max-width: 1400px; padding: 24px; font-size: 10.5pt; line-height: 1.45; }
h1 { font-size: 20pt; margin: 0 0 6px; }
h2 { font-size: 14pt; margin: 28px 0 8px; padding-bottom: 4px; border-bottom: 2px solid #d5dbe1; break-after: avoid; }
h3 { font-size: 11.5pt; margin: 20px 0 6px; break-after: avoid; }
p { margin: 6px 0 10px; }
code { font-family: Consolas, "Cascadia Mono", monospace; font-size: 0.92em; background: #eef1f4; padding: 0 3px;
       border-radius: 3px; }
table { border-collapse: collapse; width: 100%; margin: 6px 0 14px; font-size: 8.4pt; font-variant-numeric: tabular-nums; }
th, td { padding: 3px 6px; border-bottom: 1px solid #e3e7eb; vertical-align: top; }
th { background: #eef1f4; font-weight: 600; text-align: left; white-space: normal; border-bottom: 1px solid #c9d0d7; }
td { white-space: nowrap; }
td:first-child, td:nth-child(2) { white-space: normal; }
tr:nth-child(even) td { background: #f8f9fb; }
tr { break-inside: avoid; }
.page-break { break-before: page; }
thead { display: table-header-group; }
@media screen { .wrap { overflow-x: auto; } }
@media print { body { padding: 0; max-width: none; font-size: 9.5pt; } table { font-size: 7.6pt; } th, td { padding: 2px 4px; } }
ul { margin: 4px 0 10px; padding-left: 22px; }
li { margin: 2px 0; }
"""


def is_list_item(line):
    s = line.lstrip()
    if s.startswith(("- ", "* ", "+ ")):
        return True
    digits = s.split(". ", 1)[0]
    return digits.isdigit() and s.startswith(digits + ". ")


def loosen_lists(text):
    """A blank line before a list that follows a line of text. Python-Markdown needs it to see the list (GitHub
    doesn't), so text written for GitHub would otherwise run its lists together into one paragraph."""
    out = []
    for line in text.splitlines():
        if is_list_item(line) and not line[:1].isspace() and out and out[-1].strip() \
                and not is_list_item(out[-1]) and not out[-1][:1].isspace():
            out.append("")
        out.append(line)
    return "\n".join(out)


def find_browser():
    if os.environ.get("BROWSER") and os.path.exists(os.environ["BROWSER"]):
        return os.environ["BROWSER"]
    candidates = [
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    ]
    for c in candidates:
        if os.path.exists(c):
            return c
    for name in ("msedge", "google-chrome", "chromium", "chromium-browser", "chrome"):
        if shutil.which(name):
            return shutil.which(name)
    return None


def main():
    args = sys.argv[1:]
    pdf = "--no-pdf" not in args
    keep_html = "--keep-html" in args or not pdf
    css = CSS.replace("A4 landscape", "A4 portrait") if "--portrait" in args else CSS
    args = [a for a in args if a not in ("--no-pdf", "--keep-html", "--portrait")]
    if not args:
        sys.exit(__doc__)
    source = args[0]
    base = os.path.splitext(source)[0]
    with open(source, encoding="utf-8") as f:
        text = f.read()
    title = next((line[2:].strip() for line in text.splitlines() if line.startswith("# ")), os.path.basename(base))
    body = markdown.markdown(loosen_lists(text), extensions=["tables"])
    body = body.replace("<table>", '<div class="wrap"><table>').replace("</table>", "</table></div>")
    page = ('<!DOCTYPE html>\n<html lang="en"><head><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width, initial-scale=1">'
            "<title>%s</title><style>%s</style></head><body>\n%s\n</body></html>\n" % (html.escape(title), css, body))
    with open(base + ".html", "w", encoding="utf-8") as f:
        f.write(page)
    if keep_html:
        print("Wrote " + base + ".html")

    if not pdf:
        return
    browser = find_browser()
    if not browser:
        sys.exit("No Edge or Chrome found for the PDF (set BROWSER); the HTML is written.")
    out = os.path.abspath(base + ".pdf")
    # Printed to a new file, then moved over the old one: the browser gives no error when it can't write the file
    # (e.g. the old PDF is open in a viewer), so the move is what reveals it.
    printed = os.path.abspath(base + ".printing.pdf")
    with tempfile.TemporaryDirectory() as profile:
        subprocess.run([browser, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                        "--user-data-dir=" + profile, "--print-to-pdf=" + printed,
                        "file:///" + os.path.abspath(base + ".html").replace("\\", "/")],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
    if not keep_html:
        os.remove(base + ".html")
    if not os.path.exists(printed):
        sys.exit("The browser didn't produce a PDF.")
    try:
        os.replace(printed, out)
    except PermissionError:
        os.remove(printed)
        sys.exit("Can't overwrite " + out + ": it's open somewhere (a PDF viewer?). Close it and run again.")
    print("Wrote " + out)


if __name__ == "__main__":
    main()
