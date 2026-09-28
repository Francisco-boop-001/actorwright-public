from __future__ import annotations

import xml.etree.ElementTree as ET
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
RENDERING_PROJECT = (
    REPOSITORY_ROOT / "src" / "NpcManager.Rendering" / "NpcManager.Rendering.csproj"
)


def test_embedded_python_renderers_parse_and_match_canonical_sources() -> None:
    project = ET.parse(RENDERING_PROJECT)
    embedded_scripts: list[Path] = []

    for element in project.iter():
        if element.tag.rsplit("}", 1)[-1] != "EmbeddedResource":
            continue
        include = element.attrib.get("Include")
        if include is None or not include.lower().endswith(".py"):
            continue
        embedded_scripts.append(
            (RENDERING_PROJECT.parent / include.replace("\\", "/")).resolve()
        )

    assert embedded_scripts, "NpcManager.Rendering embeds no Python renderer scripts"

    for script in embedded_scripts:
        source_bytes = script.read_bytes()
        compile(source_bytes, f"actorwright-embedded/{script.name}", "exec")

        canonical = REPOSITORY_ROOT / "tools" / "rendering" / script.name
        if canonical.is_file():
            assert source_bytes == canonical.read_bytes(), (
                f"embedded renderer {script.name} differs from its canonical tools source"
            )
