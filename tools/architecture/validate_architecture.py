#!/usr/bin/env python3
"""Validate the production project dependency graph and boundary conventions."""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

EXPECTED: dict[str, set[str]] = {
    "NpcManager.Domain.csproj": set(),
    "NpcManager.Application.csproj": {"NpcManager.Domain.csproj"},
    "NpcManager.Infrastructure.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Rendering.csproj"},
    "NpcManager.Formats.Bethesda.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj"},
    "NpcManager.Presets.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj"},
    "NpcManager.Assets.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj"},
    "NpcManager.FaceGen.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj"},
    "NpcManager.BodyGen.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj"},
    "NpcManager.Pipeline.csproj": {"NpcManager.Application.csproj", "NpcManager.Assets.csproj", "NpcManager.Domain.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Infrastructure.csproj"},
    "NpcManager.Rendering.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj"},
    "NpcManager.Verification.csproj": {"NpcManager.Domain.csproj"},
    "NpcManager.Cli.csproj": {"NpcManager.Application.csproj", "NpcManager.Assets.csproj", "NpcManager.Domain.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Presets.csproj", "NpcManager.FaceGen.csproj", "NpcManager.BodyGen.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Rendering.csproj"},
    "NpcManager.Desktop.csproj": {"NpcManager.Application.csproj", "NpcManager.Assets.csproj", "NpcManager.Domain.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Presets.csproj", "NpcManager.FaceGen.csproj", "NpcManager.BodyGen.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Rendering.csproj"},
    "NpcManager.Architecture.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.Assets.csproj", "NpcManager.Domain.csproj", "NpcManager.FaceGen.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Gate2.PipelineIntegration.Tests.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Presets.csproj", "NpcManager.Rendering.csproj"},
    "NpcManager.Assets.Tests.csproj": {"NpcManager.Assets.csproj", "NpcManager.Application.csproj", "NpcManager.Domain.csproj", "NpcManager.FaceGen.csproj"},
    "NpcManager.BethesdaFaceRouting.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.BodyGen.csproj", "NpcManager.Domain.csproj", "NpcManager.FaceGen.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Presets.csproj"},
    "NpcManager.Cli.Tests.csproj": {"NpcManager.Cli.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Rendering.csproj"},
    "NpcManager.Desktop.Smoke.csproj": {"NpcManager.Desktop.csproj", "NpcManager.Application.csproj", "NpcManager.Domain.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.BodyGen.csproj", "NpcManager.Presets.csproj", "NpcManager.Rendering.csproj"},
    "NpcManager.FaceGen.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.Assets.csproj", "NpcManager.Cli.csproj", "NpcManager.Domain.csproj", "NpcManager.FaceGen.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Presets.csproj"},
    "NpcManager.FaceGeomOrchestration.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj", "NpcManager.Infrastructure.csproj"},
    "NpcManager.Gate1.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj", "NpcManager.FaceGen.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Pipeline.csproj"},
    "NpcManager.Gate2.PipelineIntegration.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.Assets.csproj", "NpcManager.BodyGen.csproj", "NpcManager.Domain.csproj", "NpcManager.FaceGen.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Presets.csproj"},
    "NpcManager.Gate2.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.Domain.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Presets.csproj"},
    "NpcManager.NpcCreationAppearance.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.BodyGen.csproj", "NpcManager.Domain.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.FaceGen.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Pipeline.csproj"},
    "NpcManager.ReferencePreset.Tests.csproj": {"NpcManager.Application.csproj", "NpcManager.Cli.csproj", "NpcManager.Domain.csproj", "NpcManager.FaceGen.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Infrastructure.csproj", "NpcManager.Pipeline.csproj", "NpcManager.Presets.csproj", "NpcManager.Rendering.csproj"},
}

PACKAGE_ALLOWED: dict[str, set[str]] = {
    "NpcManager.Assets.csproj": {"Mutagen.Bethesda.Skyrim"},
    "NpcManager.FaceGen.csproj": {"BCnEncoder.Net"},
    "NpcManager.Infrastructure.csproj": {
        "SkiaSharp",
        "SkiaSharp.NativeAssets.Win32",
    },
    "NpcManager.Rendering.csproj": {
        "SkiaSharp",
        "SkiaSharp.NativeAssets.Win32",
    },
    "NpcManager.Formats.Bethesda.csproj": {
        "Mutagen.Bethesda.Fallout4",
        "Mutagen.Bethesda.Skyrim",
    },
}

CSHARP_NON_CODE = re.compile(
    r'""".*?"""|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|'
    r"'(?:\\.|[^'\\])*'|//[^\r\n]*|/\*.*?\*/",
    re.DOTALL,
)

SECURITY_CODE_PATTERN = re.compile(r'"([a-z0-9]+(?:-[a-z0-9]+)+)"')
SECURITY_CODE_TERMS = re.compile(
    r"outside|protected|reparse|unsafe|alternate-data|denied|escape|root-overlap"
)
SECURITY_CODE_EXCLUSIONS = {
    "alternate-data",
    "alternate-data-stream",
    "facegen-bake-target-plugin-outside-order",
    "face-texture-protected-neck",
    "finish-core-verify-protected-subrecords",
    "npc-create-protected-mismatch",
    # These codes are security-shaped but do not flow through the legacy
    # Diagnostic classifier: journal warnings are nonfatal, workflow codec
    # failures are typed exceptions, and schema output uses a typed v2 class.
    "operation-journal-access-denied",
    "operation-journal-reparse-refused",
    "protected-appearance",
    "review-path-outside-lab",
    "review-reparse-refused",
    "root-overlap",
    "schema-output-outside-k-drive",
    "unsafe-path",
    "workflow-path-outside-lab",
    "workflow-reparse-refused",
}
SECURITY_CODE_DYNAMIC = {
    "facegeom-allowed-root-reparse",
    "facegeom-authority-manifest-reparse",
    "facegeom-carrier-reparse",
    "facegeom-chargen-reparse",
    "facegeom-output-parent-reparse",
    "facegeom-staging-root-reparse",
    "package-acceptance-outside-lab",
    "runtime-report-outside-lab",
}
SECURITY_CODE_PRODUCER_DECLARED = {
    "actor-assembly-security-refused",
    "facegeom-hair-regions-security-refusal",
    "follower-finish-proposal-security-refused",
    "follower-finish-request-security-refused",
    "preset-npc-request-security-refused",
}

SECURITY_CATALOG_PATTERN = re.compile(
    r"\bstatic\s+readonly\s+FrozenSet<string>\s+SecurityCodes\s*=\s*"
    r"new\s*\[\]\s*\{(?P<body>.*?)\}\s*\.ToFrozenSet\s*\(",
    re.DOTALL,
)

ASYNC_COMMAND_DECLARATION = re.compile(r"\bclass\s+AsyncCommand\b")
ASYNC_COMMAND_SHARED_SOURCE = Path("src/NpcManager.Desktop/AsyncCommand.cs")
ASYNC_COMMAND_GUARDED_SOURCE = Path(
    "src/NpcManager.Desktop/DesktopWorkflowReviewViewModel.cs")
STRING_CONTAINMENT_HELPER = re.compile(
    # ponytail: Inspect this narrow helper signature instead of parsing C#.
    r"\b(?:private|internal|public|protected)\s+static\s+bool\s+"
    r"\w+\s*\(\s*string\s+\w+\s*,"
    r"\s*string\s+\w+\s*\)")
RAW_STRING_CONTAINMENT_EXCEPTIONS = {
    Path("src/NpcManager.FaceGen/WindowsPinnedPath.cs"),
}
IMAGE_ELEMENT = re.compile(r"<Image\b(?P<attributes>[^>]*)>", re.DOTALL)
IMAGE_SOURCE = re.compile(
    r"\bSource\s*=\s*([\"'])(?P<source>.*?)\1", re.DOTALL)
IMAGE_BINDING = re.compile(
    r"\{Binding\s+(?:Path\s*=\s*)?"
    r"(?P<property>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)")

MUTABLE_STATIC_FIELD_PATTERN = re.compile(
    r"^\s*(?:public|internal|protected|private)?\s*static\s+"
    r"(?!readonly\b|const\b|class\b)"
    r"[^\r\n;{}()=]+?\s+[A-Za-z_]\w*\s*(?:=(?!>)|;)",
    re.MULTILINE,
)


def project_references(path: Path) -> set[str]:
    root = ET.parse(path).getroot()
    references: set[str] = set()
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1] == "ProjectReference":
            include = element.attrib.get("Include", "")
            references.add(Path(include).name)
    return references


def package_references(path: Path) -> set[str]:
    root = ET.parse(path).getroot()
    references: set[str] = set()
    for element in root.iter():
        if element.tag.rsplit("}", 1)[-1] == "PackageReference":
            include = element.attrib.get("Include", "")
            if include:
                references.add(include)
    return references


def validate_cli_exit_classification(
    project_root: Path,
    errors: list[str],
) -> None:
    cli_root = project_root / "src" / "NpcManager.Cli"
    classifier = cli_root / "DiagnosticExitCodeClassifier.cs"
    catalog = (
        project_root
        / "src"
        / "NpcManager.Application"
        / "ProtocolDiagnostics.cs"
    )
    catalog_text = catalog.read_text(encoding="utf-8")
    catalog_match = SECURITY_CATALOG_PATTERN.search(catalog_text)
    if catalog_match is None:
        errors.append(
            "ProtocolDiagnosticClassifier: cannot locate closed SecurityCodes catalog"
        )
        registered: set[str] = set()
    else:
        registered = set(
            SECURITY_CODE_PATTERN.findall(catalog_match.group("body"))
        )
    candidates: set[str] = set()
    for source in sorted((project_root / "src").rglob("*.cs")):
        if source == classifier:
            continue
        candidates.update(
            code
            for code in SECURITY_CODE_PATTERN.findall(
                source.read_text(encoding="utf-8")
            )
            if SECURITY_CODE_TERMS.search(code)
        )
    expected = (
        (candidates - SECURITY_CODE_EXCLUSIONS)
        | SECURITY_CODE_DYNAMIC
        | SECURITY_CODE_PRODUCER_DECLARED
    )
    missing = sorted(expected - registered)
    extra = sorted(registered - expected)
    if missing:
        errors.append(
            "DiagnosticExitCodeClassifier: missing registered security codes: "
            + ", ".join(missing)
        )
    if extra:
        errors.append(
            "DiagnosticExitCodeClassifier: extra registered security codes: "
            + ", ".join(extra)
        )

    for source in sorted(cli_root.glob("*.cs")):
        if source == classifier:
            continue
        text = source.read_text(encoding="utf-8")
        code = CSHARP_NON_CODE.sub("", text)
        if ".Code.Contains(" in code:
            errors.append(
                f"{source.relative_to(project_root)}: diagnostic exit codes "
                "must not use substring classification"
            )
        if "CommandExitCode.SecurityRefusal" in code:
            errors.append(
                f"{source.relative_to(project_root)}: security refusal must "
                "route through DiagnosticExitCodeClassifier"
            )
        if (
            re.search(r"\bDiagnostics?\b", code)
            and "CommandExitCode.ValidationFailure" in code
            and "DiagnosticExitCodeClassifier." not in code
        ):
            errors.append(
                f"{source.relative_to(project_root)}: diagnostic result exits "
                "must route through DiagnosticExitCodeClassifier"
            )


def validate_core_api_source(
    source: Path,
    text: str,
    errors: list[str],
) -> None:
    # Comments, command names, and diagnostics may legitimately contain words
    # such as "dynamic" or "object-template". Inspect C# code tokens only.
    code = CSHARP_NON_CODE.sub("", text)
    if re.search(r"\bdynamic\b", code):
        errors.append(f"{source}: dynamic is forbidden in core APIs")
    if re.search(r"\bobject\??\b", code):
        errors.append(f"{source}: object is forbidden in core APIs")
    if MUTABLE_STATIC_FIELD_PATTERN.search(code):
        errors.append(f"{source}: possible mutable static state requires review")


def _csharp_effect_code(text: str) -> str:
    """Remove C# non-code while retaining executable interpolation holes."""

    def raw_start(index: int) -> tuple[int, int, int] | None:
        cursor = index
        dollars = 0
        while cursor < len(text) and text[cursor] == "$":
            dollars += 1
            cursor += 1
        quote_start = cursor
        while cursor < len(text) and text[cursor] == '"':
            cursor += 1
        quotes = cursor - quote_start
        return (dollars, quotes, cursor) if quotes >= 3 else None

    def skip_quoted(index: int, verbatim: bool, quote: str) -> int:
        cursor = index + 1
        while cursor < len(text):
            if verbatim and text[cursor] == quote:
                if cursor + 1 < len(text) and text[cursor + 1] == quote:
                    cursor += 2
                    continue
                return cursor + 1
            if not verbatim and text[cursor] == "\\":
                cursor += 2
                continue
            if text[cursor] == quote:
                return cursor + 1
            cursor += 1
        return cursor

    def scan_interpolated(index: int, verbatim: bool) -> tuple[str, int]:
        quote_index = index + (1 if text.startswith("$\"", index) else 2)
        cursor = quote_index + 1
        fragments: list[str] = []
        while cursor < len(text):
            if verbatim and text[cursor] == '"':
                if cursor + 1 < len(text) and text[cursor + 1] == '"':
                    cursor += 2
                    continue
                return " ".join(fragments), cursor + 1
            if not verbatim and text[cursor] == "\\":
                cursor += 2
                continue
            if not verbatim and text[cursor] == '"':
                return " ".join(fragments), cursor + 1
            if text.startswith("{{", cursor) or text.startswith("}}", cursor):
                cursor += 2
                continue
            if text[cursor] == "{":
                fragment, cursor = scan_code(cursor + 1, 1)
                fragments.append(fragment)
                continue
            cursor += 1
        return " ".join(fragments), cursor

    def scan_raw(
        index: int,
        dollars: int,
        quotes: int,
        content_start: int,
    ) -> tuple[str, int]:
        cursor = content_start
        delimiter = '"' * quotes
        opening = "{" * dollars
        fragments: list[str] = []
        while cursor < len(text):
            if text.startswith(delimiter, cursor):
                return " ".join(fragments), cursor + quotes
            if dollars and text.startswith(opening, cursor):
                fragment, cursor = scan_code(cursor + dollars, dollars)
                fragments.append(fragment)
                continue
            cursor += 1
        return " ".join(fragments), cursor

    def scan_code(index: int, closing_braces: int = 0) -> tuple[str, int]:
        cursor = index
        brace_depth = 0
        output: list[str] = []
        closing = "}" * closing_braces
        while cursor < len(text):
            if (closing_braces and brace_depth == 0 and
                    text.startswith(closing, cursor)):
                return "".join(output), cursor + closing_braces
            if text.startswith("//", cursor):
                newline = text.find("\n", cursor + 2)
                cursor = len(text) if newline < 0 else newline
                output.append(" ")
                continue
            if text.startswith("/*", cursor):
                end = text.find("*/", cursor + 2)
                cursor = len(text) if end < 0 else end + 2
                output.append(" ")
                continue

            raw = raw_start(cursor)
            if raw is not None:
                dollars, quotes, content_start = raw
                fragment, cursor = scan_raw(
                    cursor, dollars, quotes, content_start)
                output.extend((" ", fragment, " "))
                continue
            if text.startswith(("$@\"", "@$\""), cursor):
                fragment, cursor = scan_interpolated(cursor, True)
                output.extend((" ", fragment, " "))
                continue
            if text.startswith("$\"", cursor):
                fragment, cursor = scan_interpolated(cursor, False)
                output.extend((" ", fragment, " "))
                continue
            if text.startswith("@\"", cursor):
                cursor = skip_quoted(cursor + 1, True, '"')
                output.append(" ")
                continue
            if text[cursor] == '"':
                cursor = skip_quoted(cursor, False, '"')
                output.append(" ")
                continue
            if text[cursor] == "'":
                cursor = skip_quoted(cursor, False, "'")
                output.append(" ")
                continue

            if text[cursor] == "{":
                brace_depth += 1
            elif text[cursor] == "}" and brace_depth:
                brace_depth -= 1
            output.append(text[cursor])
            cursor += 1
        return "".join(output), cursor

    return scan_code(0)[0]


def validate_protocol_effect_construction_boundary(
    project_root: Path,
    errors: list[str],
) -> None:
    boundary = (project_root / "src" / "NpcManager.Application" /
                "ProtocolEffect.cs")
    sources = sorted(
        path for path in (project_root / "src").rglob("*.cs")
        if not {"bin", "obj"}.intersection(path.parts)
    )
    raw_pattern = re.compile(
        r"\bnew\s+(?:(?:global::)?[A-Za-z_]\w*(?:::|\.))*"
        r"ProtocolEffect\s*\(")
    alias_pattern = re.compile(
        r"\busing\s+([A-Za-z_]\w*)\s*=\s*"
        r"(?:(?:global::)?[A-Za-z_]\w*(?:::|\.))*ProtocolEffect\s*;")
    new_token_pattern = re.compile(r"(?<!@)\bnew\b")
    throw_new_pattern = re.compile(
        r"(?<!@)\bthrow\s+(?<!@)\bnew\b")
    unchecked_pattern = re.compile(r"\bCreateUncheckedForTesting\s*\(")
    unchecked_declaration_pattern = re.compile(
        r"\binternal\s+static\s+ProtocolEffect\s+"
        r"CreateUncheckedForTesting\s*\(")

    if not boundary.is_file():
        errors.append(
            f"{boundary.relative_to(project_root)}: required ProtocolEffect "
            "construction boundary is missing")
    for source in sources:
        code = _csharp_effect_code(source.read_text(encoding="utf-8"))
        relative = source.relative_to(project_root)
        if source == boundary:
            raw_count = (len(new_token_pattern.findall(code)) -
                         len(throw_new_pattern.findall(code)))
            if raw_count != 1:
                errors.append(
                    f"{relative}: expected exactly one raw ProtocolEffect "
                    f"construction, found {raw_count}")
        else:
            raw_count = len(raw_pattern.findall(code))
            for alias in alias_pattern.findall(code):
                raw_count += len(re.findall(
                    rf"\bnew\s+{re.escape(alias)}\s*\(", code))
            if raw_count:
                errors.append(
                    f"{relative}: raw ProtocolEffect construction is forbidden")
        unchecked_code = (unchecked_declaration_pattern.sub("", code)
                          if source == boundary else code)
        if unchecked_pattern.search(unchecked_code):
            errors.append(
                f"{relative}: unchecked ProtocolEffect factory is test-only")


def validate_desktop_async_command_sharing(
    project_root: Path,
    errors: list[str],
) -> None:
    """Keep simple desktop async commands shared, preserving the guarded exception."""
    desktop_root = project_root / "src" / "NpcManager.Desktop"
    allowed = (
        ASYNC_COMMAND_SHARED_SOURCE,
        ASYNC_COMMAND_GUARDED_SOURCE,
    )
    counts = {source: 0 for source in allowed}

    for source in sorted(desktop_root.rglob("*.cs")):
        if {"bin", "obj"}.intersection(source.parts):
            continue
        code = CSHARP_NON_CODE.sub("", source.read_text(encoding="utf-8"))
        declarations = len(ASYNC_COMMAND_DECLARATION.findall(code))
        if declarations == 0:
            continue

        relative = source.relative_to(project_root)
        if relative not in allowed:
            errors.append(
                f"{relative}: additional AsyncCommand declaration must use "
                "the shared desktop command")
            continue
        counts[relative] = declarations

    for source, count in counts.items():
        if count != 1:
            errors.append(
                f"{source}: expected exactly one AsyncCommand declaration, found {count}")


def _csharp_helper_body(code: str, start: int) -> str:
    tail = code[start:]
    arrow = tail.find("=>")
    opening = tail.find("{")
    if arrow >= 0 and (opening < 0 or arrow < opening):
        body_start = start + arrow + 2
        end = code.find(";", body_start)
        return code[body_start:end] if end >= 0 else code[body_start:]
    if opening < 0:
        return ""

    body_start = start + opening
    depth = 0
    for index in range(body_start, len(code)):
        if code[index] == "{":
            depth += 1
        elif code[index] == "}":
            depth -= 1
            if depth == 0:
                return code[body_start:index + 1]
    return code[body_start:]


def validate_string_containment_helpers(
    project_root: Path,
    errors: list[str],
) -> None:
    """Keep raw string path helpers on the shared WorkspacePath boundary."""
    source_root = project_root / "src"
    for source in sorted(source_root.rglob("*.cs")):
        if {"bin", "obj"}.intersection(source.parts):
            continue
        code = CSHARP_NON_CODE.sub("", source.read_text(encoding="utf-8"))
        relative = source.relative_to(project_root)
        for match in STRING_CONTAINMENT_HELPER.finditer(code):
            if relative in RAW_STRING_CONTAINMENT_EXCEPTIONS:
                continue
            body = _csharp_helper_body(code, match.end())
            has_relative_path = "Path.GetRelativePath(" in body
            checks_containment = any(
                marker in body
                for marker in (
                    "Path.IsPathRooted(",
                    ".Equals(",
                    ".StartsWith(",
                    "==",
                )
            )
            if not has_relative_path or not checks_containment:
                continue
            if "WorkspacePath" not in body or ".IsUnder(" not in body:
                errors.append(
                    f"{relative}: string containment helper must delegate to WorkspacePath.IsUnder")


def validate_desktop_image_path_bindings(
    project_root: Path,
    errors: list[str],
) -> None:
    """Require the safe loader for desktop Image bindings that name a path."""
    desktop_root = project_root / "src" / "NpcManager.Desktop"
    required_converter = "Converter={StaticResource PathToImageSource}"
    for source_file in sorted(desktop_root.rglob("*.xaml")):
        if {"bin", "obj"}.intersection(source_file.parts):
            continue
        text = source_file.read_text(encoding="utf-8")
        relative = source_file.relative_to(project_root)
        for image in IMAGE_ELEMENT.finditer(text):
            source_match = IMAGE_SOURCE.search(image.group("attributes"))
            if source_match is None:
                continue
            binding = IMAGE_BINDING.search(source_match.group("source"))
            if binding is None:
                continue
            members = binding.group("property").split(".")
            if members[-1] == "Value" and len(members) > 1:
                members.pop()
            if not members[-1].endswith("Path"):
                continue
            if required_converter not in source_match.group("source"):
                errors.append(
                    f"{relative}: file-path Image.Source bindings must use PathToImageSource")


def main() -> int:
    project_root = Path(__file__).resolve().parents[2]
    errors: list[str] = []
    projects = {
        path.name: path
        for path in project_root.glob("src/*/*.csproj")
        if not path.name.endswith("_wpftmp.csproj")
    }
    projects.update({path.name: path for path in project_root.glob("tests/*/*.csproj")})

    missing = sorted(set(EXPECTED) - projects.keys())
    if missing:
        errors.append(f"missing expected projects: {', '.join(missing)}")
    unexpected = sorted(projects.keys() - set(EXPECTED))
    if unexpected:
        errors.append(f"unexpected projects require an architecture decision: {', '.join(unexpected)}")

    for name, expected in EXPECTED.items():
        path = projects.get(name)
        if path is None:
            continue
        try:
            actual = project_references(path)
        except (OSError, ET.ParseError) as exc:
            errors.append(f"{name}: cannot parse project: {exc}")
            continue
        if actual != expected:
            errors.append(f"{name}: references {sorted(actual)}, expected {sorted(expected)}")
        try:
            actual_packages = package_references(path)
        except (OSError, ET.ParseError) as exc:
            errors.append(f"{name}: cannot parse package references: {exc}")
            continue
        expected_packages = PACKAGE_ALLOWED.get(name, set())
        if actual_packages != expected_packages:
            errors.append(
                f"{name}: packages {sorted(actual_packages)}, expected {sorted(expected_packages)}"
            )

    for source_root in (project_root / "src" / "NpcManager.Domain", project_root / "src" / "NpcManager.Application"):
        for source in source_root.glob("*.cs"):
            text = source.read_text(encoding="utf-8")
            validate_core_api_source(
                source.relative_to(project_root),
                text,
                errors,
            )

    validate_cli_exit_classification(project_root, errors)
    validate_protocol_effect_construction_boundary(project_root, errors)
    validate_desktop_async_command_sharing(project_root, errors)
    validate_string_containment_helpers(project_root, errors)
    validate_desktop_image_path_bindings(project_root, errors)

    if errors:
        print("RESULT FAIL")
        for error in errors:
            print(f"  - {error}")
        return 1

    print(f"RESULT PASS projects={len(EXPECTED)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
