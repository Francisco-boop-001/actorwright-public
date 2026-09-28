#!/usr/bin/env python3
"""Validate complete, truthful classification of standalone test selectors."""

from __future__ import annotations

import json
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path
from typing import Any


ALLOWED = {"runnable", "fixture-bound", "unverified"}
REGISTRY = "tests/standalone-test-matrix.json"
SOLUTION = "Actorwright.sln"
REGISTRY_FIELDS = {"schemaVersion", "scope", "tests"}
ENTRY_FIELDS = {
    "id",
    "project",
    "arguments",
    "classification",
    "dependencies",
    "reason",
}
OPTIONAL_ENTRY_FIELDS = {"targetFramework", "timeoutSeconds"}


def _skip_regular_string(source: str, start: int) -> tuple[int, str]:
    """Return the end and decoded-enough value of a regular C# string."""
    value: list[str] = []
    index = start + 1
    while index < len(source):
        character = source[index]
        if character == "\\":
            if index + 1 >= len(source):
                raise ValueError("unterminated regular string literal")
            value.append(source[index + 1])
            index += 2
            continue
        if character == '"':
            return index + 1, "".join(value)
        if character in "\r\n":
            raise ValueError("unterminated regular string literal")
        value.append(character)
        index += 1
    raise ValueError("unterminated regular string literal")


def _skip_verbatim_string(source: str, quote_index: int) -> int:
    index = quote_index + 1
    while index < len(source):
        if source[index] != '"':
            index += 1
            continue
        if index + 1 < len(source) and source[index + 1] == '"':
            index += 2
            continue
        return index + 1
    raise ValueError("unterminated verbatim string literal")


def _skip_raw_string(source: str, start: int) -> int:
    quote_count = 0
    while start + quote_count < len(source) and source[start + quote_count] == '"':
        quote_count += 1
    delimiter = '"' * quote_count
    end = source.find(delimiter, start + quote_count)
    if end < 0:
        raise ValueError("unterminated raw string literal")
    return end + quote_count


def _skip_character_literal(source: str, start: int) -> int:
    index = start + 1
    while index < len(source):
        if source[index] == "\\":
            index += 2
            continue
        if source[index] == "'":
            return index + 1
        if source[index] in "\r\n":
            raise ValueError("unterminated character literal")
        index += 1
    raise ValueError("unterminated character literal")


def _csharp_tokens(source: str) -> list[tuple[str, str]]:
    """Tokenize only the C# forms needed to identify literal test routes."""
    tokens: list[tuple[str, str]] = []
    index = 0
    while index < len(source):
        character = source[index]
        if character.isspace():
            index += 1
            continue
        if source.startswith("//", index):
            newline = source.find("\n", index + 2)
            index = len(source) if newline < 0 else newline + 1
            continue
        if source.startswith("/*", index):
            end = source.find("*/", index + 2)
            if end < 0:
                raise ValueError("unterminated block comment")
            index = end + 2
            continue
        if source.startswith('@"', index):
            index = _skip_verbatim_string(source, index + 1)
            continue
        if source.startswith('$@"', index) or source.startswith('@$"', index):
            index = _skip_verbatim_string(source, index + 2)
            continue
        if character == '$':
            dollar_end = index
            while dollar_end < len(source) and source[dollar_end] == '$':
                dollar_end += 1
            quote_end = dollar_end
            while quote_end < len(source) and source[quote_end] == '"':
                quote_end += 1
            quote_count = quote_end - dollar_end
            if quote_count >= 3:
                index = _skip_raw_string(source, dollar_end)
                continue
            if quote_count == 1:
                index, _ = _skip_regular_string(source, dollar_end)
                continue
        if source.startswith('"""', index):
            index = _skip_raw_string(source, index)
            continue
        if character == '"':
            index, value = _skip_regular_string(source, index)
            tokens.append(("string", value))
            continue
        if character == "'":
            index = _skip_character_literal(source, index)
            continue
        operator = next(
            (candidate for candidate in ("&&", "==", ">=")
             if source.startswith(candidate, index)),
            None,
        )
        if operator is not None:
            tokens.append(("operator", operator))
            index += len(operator)
            continue
        if character.isdigit():
            end = index + 1
            while end < len(source) and (
                source[end].isdigit() or source[end] == "_"
            ):
                end += 1
            tokens.append(("number", source[index:end].replace("_", "")))
            index = end
            continue
        if character == "_" or character.isalpha():
            end = index + 1
            while end < len(source) and (
                source[end] == "_" or source[end].isalnum()
            ):
                end += 1
            tokens.append(("identifier", source[index:end]))
            index = end
            continue
        if character in "[]{}(),:;=.<>!":
            tokens.append(("punctuation", character))
        index += 1
    return tokens


def _selectors(source: str) -> list[tuple[str, int, int | None]]:
    """Find dispatcher routes and their accepted argument counts."""
    tokens = _csharp_tokens(source)
    constants: dict[str, str] = {}
    for index in range(len(tokens) - 4):
        candidate = tokens[index:index + 5]
        if (
            candidate[0:2] == [
                ("identifier", "const"), ("identifier", "string"),
            ]
            and candidate[2][0] == "identifier"
            and candidate[2][1].startswith("Selector_")
            and candidate[3] == ("punctuation", "=")
            and candidate[4][0] == "string"
        ):
            name = candidate[2][1]
            selector = candidate[4][1]
            if not selector.startswith("--"):
                raise ValueError(
                    f"selector constant {name} must start with '--'")
            if name in constants:
                raise ValueError(f"duplicate selector constant: {name}")
            constants[name] = selector

    for index in range(len(tokens) - 1):
        name = tokens[index]
        if (
            name[0] == "identifier"
            and name[1].startswith("Selector_")
            and tokens[index + 1] == ("punctuation", "=")
            and name[1] not in constants
        ):
            raise ValueError(
                f"selector field {name[1]} must be a string constant")

    used_constants: set[str] = set()

    def selector_value(token: tuple[str, str]) -> str | None:
        if token[0] == "string":
            value = token[1]
        elif token[0] == "identifier" and token[1] in constants:
            used_constants.add(token[1])
            value = constants[token[1]]
        else:
            return None
        return value if value.startswith("--") else None

    selectors: list[tuple[str, int, int | None]] = []
    for index in range(len(tokens) - 4):
        candidate = tokens[index:index + 4]
        collection_route = (
            candidate[0] == ("identifier", "args")
            and candidate[1] == ("identifier", "is")
            and candidate[2] == ("punctuation", "[")
            and tokens[index + 4] in {
                ("punctuation", "]"), ("punctuation", ",")
            }
        )
        selector = selector_value(candidate[3]) if collection_route else None
        if selector is not None:
            end = index + 4
            depth = 1
            argument_count = 1
            while end < len(tokens) and depth:
                token = tokens[end]
                if token == ("punctuation", "["):
                    depth += 1
                elif token == ("punctuation", "]"):
                    depth -= 1
                elif token == ("punctuation", ",") and depth == 1:
                    argument_count += 1
                end += 1
            if depth:
                raise ValueError("unterminated args collection pattern")
            selectors.append((selector, argument_count, argument_count))

    route_length = 12
    for index in range(len(tokens) - route_length + 1):
        candidate = tokens[index:index + route_length]
        length_route = (
            candidate[0:3] == [
                ("identifier", "args"),
                ("punctuation", "."),
                ("identifier", "Length"),
            ]
            and candidate[3][0] == "operator"
            and candidate[3][1] in {"==", ">="}
            and candidate[4][0] == "number"
            and candidate[5] == ("operator", "&&")
            and candidate[6:10] == [
                ("identifier", "args"),
                ("punctuation", "["),
                ("number", "0"),
                ("punctuation", "]"),
            ]
            and candidate[10] == ("operator", "==")
        )
        selector = selector_value(candidate[11]) if length_route else None
        if selector is not None:
            argument_count = int(candidate[4][1])
            maximum = argument_count if candidate[3][1] == "==" else None
            selectors.append((selector, argument_count, maximum))

    for index in range(len(tokens) - 9):
        candidate = tokens[index:index + 10]
        contains_route = (
            candidate[0:4] == [
                ("identifier", "args"),
                ("punctuation", "."),
                ("identifier", "Contains"),
                ("punctuation", "("),
            ]
            and candidate[5] == ("punctuation", ",")
            and candidate[6:9] == [
                ("identifier", "StringComparer"),
                ("punctuation", "."),
                ("identifier", "Ordinal"),
            ]
            and candidate[9] == ("punctuation", ")")
        )
        selector = selector_value(candidate[4]) if contains_route else None
        if selector is not None:
            selectors.append((selector, 1, None))

    unused = sorted(set(constants) - used_constants)
    if unused:
        raise ValueError(
            "selector constant is not used in a discovered route: " +
            ", ".join(unused))
    return selectors


def _solution_test_projects(root: Path) -> set[str]:
    text = (root / SOLUTION).read_text(encoding="utf-8-sig")
    projects = {
        Path(match.group(1).replace("\\", "/")).as_posix()
        for match in re.finditer(
            r'^Project\("[^"]+"\)\s*=\s*"[^"]+",\s*"([^"\r\n]+\.csproj)"',
            text,
            re.MULTILINE | re.IGNORECASE,
        )
        if Path(match.group(1).replace("\\", "/")).parts[0].casefold()
        == "tests"
    }
    if not projects:
        raise ValueError(f"solution contains no test projects: {SOLUTION}")
    missing = sorted(project for project in projects if not (root / project).is_file())
    if missing:
        raise ValueError("solution test project is missing: " + ", ".join(missing))
    return projects


def _project_sources(root: Path, project: str) -> list[Path]:
    directory = (root / project).parent
    return sorted(
        source for source in directory.rglob("*.cs")
        if not {"bin", "obj"}.intersection(part.casefold() for part in source.parts)
    )


def _scenario_inventory(
    tokens: list[tuple[str, str]], project: str,
) -> set[str]:
    reserved: list[str] = []
    for index in range(len(tokens) - 2):
        if (tokens[index] == ("identifier", "ReservedSelectors") and
                tokens[index + 1] == ("punctuation", "=") and
                tokens[index + 2] == ("punctuation", "[")):
            end = index + 3
            while end < len(tokens) and tokens[end] != ("punctuation", "]"):
                if tokens[end][0] == "string":
                    reserved.append(tokens[end][1])
                end += 1
            if end == len(tokens):
                raise ValueError(f"unterminated ReservedSelectors in {project}")

    implementations = [
        tokens[index + 3][1]
        for index in range(len(tokens) - 3)
        if tokens[index] == ("identifier", "Selector")
        and tokens[index + 1:index + 3] == [
            ("punctuation", "="), ("punctuation", ">"),
        ]
        and tokens[index + 3][0] == "string"
        and tokens[index + 3][1].startswith("--test-")
    ]

    if not reserved and not implementations:
        return set()
    if not reserved:
        raise ValueError(f"scenario Selector properties have no ReservedSelectors in {project}")
    if len(reserved) != len(set(reserved)):
        raise ValueError(f"duplicate reserved scenario selector in {project}")
    if len(implementations) != len(set(implementations)):
        raise ValueError(f"duplicate scenario implementation selector in {project}")
    if set(reserved) != set(implementations):
        missing = sorted(set(reserved) - set(implementations))
        unreserved = sorted(set(implementations) - set(reserved))
        details = []
        if missing:
            details.append("missing implementations: " + ", ".join(missing))
        if unreserved:
            details.append("unreserved implementations: " + ", ".join(unreserved))
        raise ValueError(
            f"scenario registry mismatch in {project}: " + "; ".join(details))
    if any(not selector.startswith("--test-") for selector in reserved):
        raise ValueError(f"invalid reserved scenario selector in {project}")
    return set(implementations)


def discover(root: Path) -> dict[tuple[str, str], tuple[int, int | None]]:
    """Return literal routes and accepted argument counts for solution test projects."""
    routes: dict[tuple[str, str], tuple[int, int | None]] = {}
    for project in sorted(_solution_test_projects(root)):
        program_sources: list[str] = []
        tokens: list[tuple[str, str]] = []
        for source in _project_sources(root, project):
            source_text = source.read_text(encoding="utf-8")
            if (not source.name.casefold().startswith("program") and
                    "ReservedSelectors" not in source_text and
                    not re.search(r"\bSelector\s*=>", source_text)):
                continue
            source_tokens = _csharp_tokens(source_text)
            if source.name.casefold().startswith("program"):
                program_sources.append(source_text)
            tokens.extend(source_tokens)
        direct = _selectors("\n".join(program_sources)) if program_sources else []
        for selector, minimum, maximum in direct:
            route = (selector, project)
            if route in routes:
                raise ValueError(f"duplicate selector route: {selector} in {project}")
            routes[route] = (minimum, maximum)
        for selector in _scenario_inventory(tokens, project):
            routes.setdefault((selector, project), (1, 1))
    return routes


def _is_executable_project(root: Path, project: str) -> bool:
    try:
        project_xml = ET.parse(root / project).getroot()
    except (ET.ParseError, OSError) as exc:
        raise ValueError(f"cannot read test project {project}: {exc}") from exc
    output_types = {
        (element.text or "").strip().casefold()
        for element in project_xml.iter()
        if element.tag.rsplit("}", 1)[-1].casefold() == "outputtype"
    }
    return "exe" in output_types


def _load_json(path: Path) -> Any:
    def unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        value: dict[str, Any] = {}
        for key, child in pairs:
            if key in value:
                raise ValueError(f"duplicate JSON key: {key}")
            value[key] = child
        return value

    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique)


def _selector(entry: object, index: int) -> str:
    if not isinstance(entry, dict):
        return f"<entry-{index}>"
    arguments = entry.get("arguments")
    if (
        isinstance(arguments, list)
        and arguments
        and isinstance(arguments[0], str)
    ):
        return arguments[0]
    identifier = entry.get("id")
    return str(identifier) if isinstance(identifier, str) else f"<entry-{index}>"


def validate(root: Path) -> list[str]:
    """Return sorted policy errors; an empty list means the registry is valid."""
    errors: list[str] = []
    try:
        solution_projects = _solution_test_projects(root)
        routes = discover(root)
    except (OSError, UnicodeError, ValueError) as exc:
        return [f"selector discovery failed: {exc}"]

    try:
        registry = _load_json(root / REGISTRY)
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError) as exc:
        return [f"invalid standalone test registry: {exc}"]

    if not isinstance(registry, dict):
        return ["standalone test registry must be a JSON object"]
    if set(registry) != REGISTRY_FIELDS:
        errors.append("registry field set mismatch")
    if registry.get("schemaVersion") != 2:
        errors.append("registry schemaVersion must be 2")
    if not isinstance(registry.get("scope"), str) or not registry["scope"].strip():
        errors.append("registry scope must be a nonempty string")
    entries = registry.get("tests")
    if not isinstance(entries, list):
        errors.append("registry tests must be an array")
        entries = []

    selectors = [_selector(entry, index) for index, entry in enumerate(entries)]
    valid_ids = [
        entry["id"]
        for entry in entries
        if isinstance(entry, dict)
        and isinstance(entry.get("id"), str)
        and entry["id"].strip()
    ]
    for identifier, count in Counter(valid_ids).items():
        if count > 1:
            errors.append(f"duplicate registry id: {identifier}")
    registry_routes = [
        (selector, entry["project"])
        for selector, entry in zip(selectors, entries)
        if isinstance(entry, dict)
        and isinstance(entry.get("project"), str)
        and entry["project"].strip()
        and isinstance(entry.get("arguments"), list)
        and bool(entry["arguments"])
        and all(
            isinstance(argument, str) and argument.strip()
            for argument in entry["arguments"])
    ]
    for (selector, _project), count in Counter(registry_routes).items():
        if count > 1:
            errors.append(f"duplicate selector: {selector}")

    route_projects = {project for _selector, project in routes}
    classified: set[tuple[str, str]] = set()
    default_projects: set[str] = set()
    for index, entry in enumerate(entries):
        selector = selectors[index]
        if not isinstance(entry, dict):
            errors.append(f"registry entry must be an object: {selector}")
            continue
        if not ENTRY_FIELDS.issubset(entry) or not set(entry).issubset(
            ENTRY_FIELDS | OPTIONAL_ENTRY_FIELDS
        ):
            errors.append(f"registry entry field set mismatch: {selector}")

        identifier = entry.get("id")
        if not isinstance(identifier, str) or not identifier.strip():
            errors.append(f"registry id must be a nonempty string: {selector}")

        classification = entry.get("classification")
        if not isinstance(classification, str):
            errors.append(f"registry classification must be a string: {selector}")
        elif classification not in ALLOWED:
            errors.append(f"invalid selector classification: {selector}")

        target_framework = entry.get("targetFramework")
        if "targetFramework" in entry and (
            not isinstance(target_framework, str) or not target_framework.strip()
        ):
            errors.append(
                f"registry targetFramework must be a nonempty string: {selector}")
        if "targetFramework" in entry and classification != "runnable":
            errors.append(
                f"registry targetFramework is only valid for runnable selectors: "
                f"{selector}")

        timeout_seconds = entry.get("timeoutSeconds")
        if "timeoutSeconds" in entry and (
            isinstance(timeout_seconds, bool)
            or not isinstance(timeout_seconds, int)
            or not 1 <= timeout_seconds <= 3600
        ):
            errors.append(
                "registry timeoutSeconds must be an integer from 1 to 3600: "
                f"{selector}")
        if "timeoutSeconds" in entry and classification != "runnable":
            errors.append(
                "registry timeoutSeconds is only valid for runnable selectors: "
                f"{selector}")

        project = entry.get("project")
        valid_project = isinstance(project, str) and bool(project.strip())
        if not valid_project:
            errors.append(f"registry project must be a nonempty string: {selector}")
        arguments = entry.get("arguments")
        valid_arguments = isinstance(arguments, list)
        if not valid_arguments:
            errors.append(
                f"registry arguments must be an array of strings: {selector}")
        elif not all(
            isinstance(argument, str) and argument.strip()
            for argument in arguments
        ):
            errors.append(
                f"registry arguments must contain nonempty strings: {selector}")
            valid_arguments = False

        if valid_arguments and arguments == []:
            if valid_project and project not in solution_projects:
                errors.append(f"registry project is not in solution: {project}")
            elif valid_project and project in solution_projects:
                try:
                    executable = _is_executable_project(root, project)
                except ValueError as exc:
                    errors.append(str(exc))
                    executable = False
                if not executable:
                    errors.append(f"default entry project is not executable: {project}")
                else:
                    if project in default_projects:
                        errors.append(f"duplicate default project entry: {project}")
                    default_projects.add(project)
        else:
            route = (selector, project)
            selector_projects = {
                route_project
                for route_selector, route_project in routes
                if route_selector == selector
            }
            if valid_project and valid_arguments and route in routes:
                classified.add(route)
                minimum, maximum = routes[route]
                argument_count = len(arguments)
                if (classification != "runnable" or arguments == [selector]) and (
                    argument_count < minimum or (
                        maximum is not None and argument_count > maximum
                    )
                ):
                    expected = (
                        f"exactly {maximum}"
                        if maximum == minimum
                        else f"at least {minimum}"
                    )
                    errors.append(
                        "registry selector argument count does not match "
                        f"discovered route (expected {expected}): {selector}")
            elif valid_project and valid_arguments and selector_projects:
                classified.update(
                    (selector, selector_project)
                    for selector_project in selector_projects)
                errors.append(f"registry project mismatch: {selector}")
            elif valid_arguments and selector.startswith("--"):
                errors.append(f"registry selector has no route: {selector}")
            elif valid_arguments:
                errors.append(f"invalid registry selector: {selector}")

        if (classification == "runnable" and valid_arguments and arguments and
                arguments != [selector]):
            errors.append(
                f"runnable selector arguments must equal ['{selector}']: {selector}")

        dependencies = entry.get("dependencies")
        reason = entry.get("reason")
        dependencies_are_strings = (
            isinstance(dependencies, list)
            and all(
                isinstance(item, str) and item.strip()
                for item in dependencies)
        )
        if not isinstance(dependencies, list):
            errors.append(
                f"registry dependencies must be an array of strings: {selector}")
        elif not dependencies_are_strings:
            errors.append(
                f"registry dependencies must contain nonempty strings: {selector}")
        valid_reason = isinstance(reason, str) and bool(reason.strip())
        if not valid_reason:
            errors.append(f"registry reason must be a nonempty string: {selector}")
        if classification in {"fixture-bound", "unverified"} and not (
            dependencies_are_strings and bool(dependencies) and valid_reason
        ):
            errors.append(
                f"{classification} selector lacks dependencies/reason: {selector}")
        if classification == "unverified":
            errors.append(
                f"unverified selector blocks canonical build: {selector}")

    for selector, _project in routes.keys() - classified:
        errors.append(f"unclassified selector: {selector}")
    for project in sorted(solution_projects):
        try:
            executable = _is_executable_project(root, project)
        except ValueError as exc:
            errors.append(str(exc))
            continue
        if executable and project not in default_projects:
            errors.append(f"unclassified default entrypoint: {project}")
        elif not executable and project not in route_projects:
            errors.append(f"unclassified test project: {project}")
    return sorted(set(errors))


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    errors = validate(root)
    if errors:
        print("\n".join(errors))
        return 1
    registry = _load_json(root / REGISTRY)
    counts = Counter(row["classification"] for row in registry["tests"])
    default_count = sum(not row["arguments"] for row in registry["tests"])
    print(
        "standalone test registry: PASS "
        f"runnable={counts['runnable']} "
        f"fixture-bound={counts['fixture-bound']} "
        f"unverified={counts['unverified']} "
        f"default-entry={default_count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
