"""The MCP tools the server registers, read from the C# sources without building them.

Each `[McpServerTool(Name = "...")]` attribute is paired with the next `public static` method and the class that
encloses it, so harness scripts can map a tool name to the C# method tests call directly.
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent
ATTRIBUTE = re.compile(r'McpServerTool\(Name\s*=\s*"([a-z0-9_]+)"')
METHOD = re.compile(r"public\s+static\s+(?:async\s+)?[\w<>\[\]?,.\s]+?\s+(\w+)\s*\(")
CLASS = re.compile(r"\bclass\s+(\w+)")


class Tool:
    def __init__(self, name, class_name, method, path):
        self.name, self.class_name, self.method, self.path = name, class_name, method, path

    def __repr__(self):
        return f"Tool({self.name!r}, {self.class_name}.{self.method})"


def registered_tools(root=ROOT):
    """Tool name -> Tool for every attribute under src/PixMcp; raises on a duplicate name or an unpaired attribute."""
    tools = {}
    for path in sorted((Path(root) / "src" / "PixMcp").rglob("*.cs")):
        text = path.read_text(encoding="utf-8-sig")
        for match in ATTRIBUTE.finditer(text):
            method = METHOD.search(text, match.end())
            classes = CLASS.findall(text, 0, match.start())
            if method is None or not classes:
                raise ValueError(f"{path}: tool {match.group(1)} has no following public static method or enclosing class")
            if match.group(1) in tools:
                raise ValueError(f"duplicate tool name {match.group(1)} in {path} and {tools[match.group(1)].path}")
            tools[match.group(1)] = Tool(match.group(1), classes[-1], method.group(1), path)
    return tools


def readme_catalog(root=ROOT):
    """Backticked pix_* names in the README '## Tool catalog' section, in order (duplicates kept)."""
    text = "\n" + (Path(root) / "README.md").read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    start = text.find("\n## Tool catalog\n")
    if start < 0:
        return None
    end = text.find("\n## ", start + 1)
    return re.findall(r"`(pix_[a-z0-9_]+)`", text[start:end if end >= 0 else len(text)])
