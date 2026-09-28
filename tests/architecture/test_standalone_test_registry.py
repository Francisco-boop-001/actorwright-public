import importlib.util
import json
import re
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[2]
VALIDATOR = ROOT / "tools" / "architecture" / "validate_standalone_test_registry.py"

PROJECTS = {
    "tests/NpcManager.Architecture.Tests/Program.cs":
        "tests/NpcManager.Architecture.Tests/NpcManager.Architecture.Tests.csproj",
    "tests/NpcManager.Cli.Tests/Program.cs":
        "tests/NpcManager.Cli.Tests/NpcManager.Cli.Tests.csproj",
    "tests/NpcManager.Desktop.Smoke/Program.cs":
        "tests/NpcManager.Desktop.Smoke/NpcManager.Desktop.Smoke.csproj",
    "tests/NpcManager.FaceGen.Tests/Program.cs":
        "tests/NpcManager.FaceGen.Tests/NpcManager.FaceGen.Tests.csproj",
    "tests/NpcManager.BethesdaFaceRouting.Tests/Program.cs":
        "tests/NpcManager.BethesdaFaceRouting.Tests/NpcManager.BethesdaFaceRouting.Tests.csproj",
    "tests/NpcManager.ReferencePreset.Tests/Program.cs":
        "tests/NpcManager.ReferencePreset.Tests/NpcManager.ReferencePreset.Tests.csproj",
}
DEFAULT_SOURCE = "tests/NpcManager.Architecture.Tests/Program.cs"
DEFAULT_PROJECT = "tests/NpcManager.Architecture.Tests/NpcManager.Architecture.Tests.csproj"


def _validator_module():
    assert VALIDATOR.is_file(), "standalone test registry validator is missing"
    spec = importlib.util.spec_from_file_location(
        "actorwright_standalone_test_registry_validator", VALIDATOR)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def write_program(root: Path, text: str) -> None:
    write_programs(root, {DEFAULT_SOURCE: text})


def write_programs(root: Path, programs: dict[str, str]) -> None:
    selected = programs or {DEFAULT_SOURCE: "public static int Main() => 0;"}
    solution_projects = []
    for index, source in enumerate(selected):
        path = root / source
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(selected[source], encoding="utf-8")
        project = (path.parent / f"{path.parent.name}.csproj").relative_to(root)
        project_path = project.as_posix()
        (root / project).write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType></PropertyGroup></Project>',
            encoding="utf-8",
        )
        project_id = f"{{00000000-0000-0000-0000-{index + 1:012d}}}"
        solution_projects.extend([
            f'Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = '
            f'"{path.parent.name}", "{project_path}", "{project_id}"',
            "EndProject",
        ])
    (root / "Actorwright.sln").write_text("\n".join(solution_projects), encoding="utf-8")


def entry(
    selector: str,
    *,
    project: str | None = None,
    classification: str = "runnable",
    arguments: list[str] | None = None,
    dependencies: list[str] | None = None,
    reason: str = "Self-contained fixture.",
) -> dict[str, object]:
    return {
        "id": selector.removeprefix("--test-"),
        "project": project or DEFAULT_PROJECT,
        "arguments": arguments if arguments is not None else [selector],
        "classification": classification,
        "dependencies": dependencies if dependencies is not None else [],
        "reason": reason,
    }


def write_registry(
    root: Path,
    entries: list[dict[str, object]],
    *,
    include_defaults: bool = True,
    **changes: object,
) -> None:
    registry_entries = list(entries)
    if include_defaults:
        solution = (root / "Actorwright.sln").read_text(encoding="utf-8")
        projects = {
            match.replace("\\", "/")
            for match in re.findall(
                r'^Project\("[^"]+"\)\s*=\s*"[^"]+",\s*"([^"\r\n]+\.csproj)"',
                solution,
                re.MULTILINE | re.IGNORECASE,
            )
            if match.replace("\\", "/").split("/", 1)[0].casefold() == "tests"
        }
        registered_defaults = {
            row.get("project") for row in registry_entries
            if row.get("arguments") == []
        }
        for project in sorted(projects - registered_defaults):
            registry_entries.append(no_selector_project(
                project,
                classification="runnable",
                dependencies=[],
                reason="The temporary test project has an accounted synthetic default entrypoint.",
            ))
    registry: dict[str, object] = {
        "schemaVersion": 2,
        "scope": "fixture",
        "tests": registry_entries,
    }
    registry.update(changes)
    path = root / "tests" / "standalone-test-matrix.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(registry), encoding="utf-8")


def no_selector_project(
    project: str,
    *,
    classification: str = "fixture-bound",
    dependencies: list[str] | None = None,
    reason: str = "Default execution requires an external authority fixture.",
) -> dict[str, object]:
    return {
        "id": "default-" + Path(project).parent.name.casefold().replace(".", "-"),
        "project": project,
        "arguments": [],
        "classification": classification,
        "dependencies": dependencies if dependencies is not None else ["fixture root"],
        "reason": reason,
    }


def validate(root: Path) -> list[str]:
    return _validator_module().validate(root)


def test_missing_selector_is_rejected(tmp_path: Path) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    write_registry(tmp_path, [])
    assert validate(tmp_path) == ["unclassified selector: --test-a"]


def test_new_solution_project_requires_a_no_selector_row(tmp_path: Path) -> None:
    project = "tests/New.Tests/New.Tests.csproj"
    write_programs(tmp_path, {
        "tests/New.Tests/Program.cs": "public static int Main() => 0;",
    })
    row = no_selector_project(
        project,
        classification="runnable",
        dependencies=[],
        reason="The parameterless Main runs only repository-local synthetic fixtures.",
    )
    write_registry(tmp_path, [row], include_defaults=False)
    assert validate(tmp_path) == []

    write_registry(tmp_path, [], include_defaults=False)
    assert validate(tmp_path) == [f"unclassified default entrypoint: {project}"]


def test_executable_project_with_selector_routes_requires_default_entrypoint(
    tmp_path: Path,
) -> None:
    project = "tests/New.Tests/New.Tests.csproj"
    selector = "--test-explicit-route"
    write_programs(tmp_path, {
        "tests/New.Tests/Program.cs": f'if (args is ["{selector}"]) return 0;',
    })
    write_registry(tmp_path, [entry(selector, project=project)], include_defaults=False)
    assert validate(tmp_path) == [f"unclassified default entrypoint: {project}"]


def test_default_entry_can_coexist_with_selector_routes(tmp_path: Path) -> None:
    project = "tests/New.Tests/New.Tests.csproj"
    selector = "--test-explicit-route"
    write_programs(tmp_path, {
        "tests/New.Tests/Program.cs": f'if (args is ["{selector}"]) return 0;',
    })
    write_registry(tmp_path, [
        entry(selector, project=project),
        no_selector_project(project, classification="runnable", dependencies=[]),
    ])
    assert validate(tmp_path) == []


def test_solution_project_selector_must_be_classified(tmp_path: Path) -> None:
    project = "tests/New.Tests/New.Tests.csproj"
    selector = "--test-new-solution-route"
    write_programs(tmp_path, {
        "tests/New.Tests/Program.cs": f'if (args is ["{selector}"]) return 0;',
    })
    write_registry(tmp_path, [entry(selector, project=project)])
    assert validate(tmp_path) == []

    write_registry(tmp_path, [])
    assert validate(tmp_path) == [f"unclassified selector: {selector}"]


def test_delegated_scenario_routes_are_discovered_and_forwarder_deduplicated(
    tmp_path: Path,
) -> None:
    selector = "--test-delegated"
    write_program(tmp_path, f'''\
if (args is ["{selector}"]) return 0;
internal interface IExampleScenario {{ string Selector {{ get; }} }}
internal static class ExampleTestRegistry {{
    private static readonly string[] ReservedSelectors = ["{selector}"];
}}
internal sealed class ExampleScenario : IExampleScenario {{
    public string Selector => "{selector}";
}}
''')
    write_registry(tmp_path, [])
    assert validate(tmp_path) == [f"unclassified selector: {selector}"]

    write_registry(tmp_path, [entry(selector)])
    assert validate(tmp_path) == []


def test_selector_constant_dispatch_preserves_parameterized_arity(
    tmp_path: Path,
) -> None:
    selector = "--test-constant-route"
    write_program(tmp_path, f'''\
private const string Selector_ConstantRoute = "{selector}";
if (args is [Selector_ConstantRoute, var input]) return 0;
''')
    row = entry(
        selector,
        classification="fixture-bound",
        arguments=[selector, "<source>"],
        dependencies=["source fixture"],
        reason="The parameterized source fixture is supplied by the parent harness.",
    )
    write_registry(tmp_path, [row])
    assert validate(tmp_path) == []

    row["arguments"] = [selector]
    write_registry(tmp_path, [row])
    assert validate(tmp_path) == [
        "registry selector argument count does not match discovered route "
        f"(expected exactly 2): {selector}",
    ]


def test_unused_selector_constant_is_rejected(tmp_path: Path) -> None:
    write_program(
        tmp_path,
        'private const string Selector_Unused = "--test-unused";\n'
        'public static int Main(string[] args) => 0;',
    )
    write_registry(tmp_path, [])
    assert validate(tmp_path) == [
        "selector discovery failed: selector constant is not used in a "
        "discovered route: Selector_Unused",
    ]


def test_delegated_scenario_must_match_its_reserved_selectors(tmp_path: Path) -> None:
    write_program(tmp_path, '''\
internal interface IExampleScenario { string Selector { get; } }
internal static class ExampleTestRegistry {
    private static readonly string[] ReservedSelectors = ["--test-reserved"];
}
internal sealed class ExampleScenario : IExampleScenario {
    public string Selector => "--test-implemented";
}
''')
    write_registry(tmp_path, [])
    assert validate(tmp_path) == [
        "selector discovery failed: scenario registry mismatch in "
        "tests/NpcManager.Architecture.Tests/NpcManager.Architecture.Tests.csproj: "
        "missing implementations: --test-reserved; "
        "unreserved implementations: --test-implemented",
    ]


def test_unknown_and_duplicate_selectors_are_rejected(tmp_path: Path) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    first = entry("--test-a")
    first["id"] = "a-first"
    duplicate = entry("--test-a")
    duplicate["id"] = "a-second"
    write_registry(tmp_path, [first, duplicate, entry("--test-b")])
    assert validate(tmp_path) == [
        "duplicate selector: --test-a",
        "registry selector has no route: --test-b",
    ]


def test_same_selector_in_different_projects_is_a_distinct_route(tmp_path: Path) -> None:
    selector = 'if (args is ["--test-shared"]) return 0;'
    write_programs(tmp_path, {
        "tests/NpcManager.Architecture.Tests/Program.cs": selector,
        "tests/NpcManager.Cli.Tests/Program.cs": selector,
    })
    architecture = entry("--test-shared")
    architecture["id"] = "architecture-shared"
    cli = entry(
            "--test-shared",
            project=PROJECTS["tests/NpcManager.Cli.Tests/Program.cs"],
        )
    cli["id"] = "cli-shared"
    write_registry(tmp_path, [architecture, cli])
    assert validate(tmp_path) == []


def test_facegen_selector_requires_its_own_registration(tmp_path: Path) -> None:
    source = "tests/NpcManager.FaceGen.Tests/Program.cs"
    write_programs(tmp_path, {
        source: 'if (args is ["--test-native-tri-topology"]) return 0;',
    })
    write_registry(tmp_path, [])
    assert validate(tmp_path) == ["unclassified selector: --test-native-tri-topology"]
    write_registry(tmp_path, [entry("--test-native-tri-topology", project=PROJECTS[source])])
    assert validate(tmp_path) == []


def test_bethesda_face_routing_selector_requires_its_own_registration(
    tmp_path: Path,
) -> None:
    source = "tests/NpcManager.BethesdaFaceRouting.Tests/Program.cs"
    write_programs(tmp_path, {
        source: 'if (args is ["--test-light-plugin-captured-hdpt-routing"]) return 0;',
    })
    write_registry(tmp_path, [])
    assert validate(tmp_path) == [
        "unclassified selector: --test-light-plugin-captured-hdpt-routing"]
    write_registry(tmp_path, [entry(
        "--test-light-plugin-captured-hdpt-routing", project=PROJECTS[source])])
    assert validate(tmp_path) == []


def test_non_runnable_entry_requires_dependency_and_reason(tmp_path: Path) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    write_registry(tmp_path, [entry(
        "--test-a",
        classification="fixture-bound",
        dependencies=[],
        reason="External fixture required.",
    )])
    assert validate(tmp_path) == [
        "fixture-bound selector lacks dependencies/reason: --test-a"]


def test_runnable_entry_requires_exact_single_selector_argument(tmp_path: Path) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    write_registry(tmp_path, [entry("--test-a", arguments=["--test-a", "extra"])])
    assert validate(tmp_path) == [
        "runnable selector arguments must equal ['--test-a']: --test-a"]


def test_target_framework_override_is_narrowly_scoped_to_runnable_rows(
    tmp_path: Path,
) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    runnable = entry("--test-a")
    runnable["targetFramework"] = "net10.0-windows"
    write_registry(tmp_path, [runnable])
    assert validate(tmp_path) == []

    runnable["classification"] = "fixture-bound"
    runnable["dependencies"] = ["external fixture"]
    write_registry(tmp_path, [runnable])
    assert validate(tmp_path) == [
        "registry targetFramework is only valid for runnable selectors: --test-a"]


def test_timeout_seconds_is_strict_and_narrowly_scoped_to_runnable_rows(
    tmp_path: Path,
) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    runnable = entry("--test-a")
    runnable["timeoutSeconds"] = 240
    write_registry(tmp_path, [runnable])
    assert validate(tmp_path) == []

    for invalid in (True, 0, 3601, 1.5):
        runnable["timeoutSeconds"] = invalid
        write_registry(tmp_path, [runnable])
        assert validate(tmp_path) == [
            "registry timeoutSeconds must be an integer from 1 to 3600: "
            "--test-a"]

    runnable["timeoutSeconds"] = 240
    runnable["classification"] = "fixture-bound"
    runnable["dependencies"] = ["external fixture"]
    write_registry(tmp_path, [runnable])
    assert validate(tmp_path) == [
        "registry timeoutSeconds is only valid for runnable selectors: "
        "--test-a"]


def test_registry_project_must_match_route_owner(tmp_path: Path) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    write_registry(tmp_path, [entry(
        "--test-a",
        project=PROJECTS["tests/NpcManager.Cli.Tests/Program.cs"],
    )])
    assert validate(tmp_path) == ["registry project mismatch: --test-a"]


def test_invalid_schema_classification_and_field_set_are_rejected(tmp_path: Path) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    malformed = entry("--test-a", classification="sometimes")
    malformed["surprise"] = True
    write_registry(tmp_path, [malformed], schemaVersion=1)
    assert validate(tmp_path) == [
        "invalid selector classification: --test-a",
        "registry entry field set mismatch: --test-a",
        "registry schemaVersion must be 2",
    ]


def test_compile_only_escape_hatch_is_rejected(tmp_path: Path) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    write_registry(tmp_path, [entry(
        "--test-a",
        classification="compile-only",
        dependencies=["not executed"],
        reason="No execution proof was collected.",
    )])
    assert validate(tmp_path) == ["invalid selector classification: --test-a"]


def test_unverified_selector_fails_closed(tmp_path: Path) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    write_registry(tmp_path, [entry(
        "--test-a",
        classification="unverified",
        dependencies=["fresh execution result"],
        reason="Execution evidence is pending.",
    )])
    assert validate(tmp_path) == [
        "unverified selector blocks canonical build: --test-a"]


def test_duplicate_registry_ids_are_rejected(tmp_path: Path) -> None:
    write_program(
        tmp_path,
        '\n'.join([
            'if (args is ["--test-a"]) return 0;',
            'if (args is ["--test-b"]) return 0;',
        ]),
    )
    first = entry("--test-a")
    second = entry("--test-b")
    second["id"] = first["id"]
    write_registry(tmp_path, [first, second])
    assert validate(tmp_path) == ["duplicate registry id: a"]


@pytest.mark.parametrize(("field", "value", "message"), [
    ("id", 7, "registry id must be a nonempty string: --test-a"),
    ("project", 7, "registry project must be a nonempty string: --test-a"),
    ("project", [], "registry project must be a nonempty string: --test-a"),
    ("project", {}, "registry project must be a nonempty string: --test-a"),
    ("arguments", "--test-a", "registry arguments must be an array of strings: a"),
    ("classification", 7, "registry classification must be a string: --test-a"),
    ("targetFramework", "", "registry targetFramework must be a nonempty string: --test-a"),
    ("dependencies", "none", "registry dependencies must be an array of strings: --test-a"),
    ("dependencies", [7], "registry dependencies must contain nonempty strings: --test-a"),
    ("reason", 7, "registry reason must be a nonempty string: --test-a"),
    ("reason", "", "registry reason must be a nonempty string: --test-a"),
])
def test_schema_two_entry_field_types_are_enforced(
    tmp_path: Path,
    field: str,
    value: object,
    message: str,
) -> None:
    write_program(tmp_path, 'if (args is ["--test-a"]) return 0;')
    malformed = entry("--test-a")
    malformed[field] = value
    write_registry(tmp_path, [malformed])
    assert message in validate(tmp_path)


def test_literal_collection_routes_include_parameterized_modes(
    tmp_path: Path,
) -> None:
    write_program(
        tmp_path,
        "\n".join([
            'if (args is ["--test-a"]) return 0;',
            'if (args is ["--legacy-mode"]) return 0;',
            'if (args is ["--test-two", var path]) return 0;',
            'var help = "--test-not-a-route";',
        ]),
    )
    write_registry(tmp_path, [
        entry("--test-a"),
        entry("--legacy-mode"),
        entry(
            "--test-two",
            arguments=["--test-two", "<path>"],
            classification="fixture-bound",
            dependencies=["caller-supplied path"],
            reason="The parameterized route requires an explicit input path.",
        ),
    ])
    assert validate(tmp_path) == []


def test_literal_args_length_and_index_routes_are_discovered(tmp_path: Path) -> None:
    project = PROJECTS["tests/NpcManager.Architecture.Tests/Program.cs"]
    write_program(
        tmp_path,
        r'''
if (args.Length == 1 && args[0] == "--native-only") return 0;
if (args.Length >= 3 && args[0] == "--parent-probe") return 0;
if (args.Length == 3 && args[0] == "--resource-repro") return 0;
if (otherargs.Length == 1 && otherargs[0] == "--other-identifier") return 0;
if (args.Length == 1 && args[1] == "--wrong-index") return 0;
// if (args.Length == 1 && args[0] == "--line-comment") return 0;
var decoy = @"args.Length == 1 && args[0] == ""--string-literal""";
''',
    )
    rows = [
        entry("--native-only", project=project),
        entry(
            "--parent-probe",
            project=project,
            arguments=["--parent-probe", "<mode>", "<runtime-root>"],
            classification="fixture-bound",
            dependencies=["parent test harness controls this child mode"],
            reason="The selector is an internal child-process route, not a standalone test.",
        ),
        entry(
            "--resource-repro",
            project=project,
            arguments=["--resource-repro", "<intake>", "<reviewed-design>"],
            classification="fixture-bound",
            dependencies=["caller-supplied authentic session documents"],
            reason="The route requires authentic caller-owned inputs.",
        ),
    ]
    write_registry(tmp_path, rows)
    assert validate(tmp_path) == []

    write_registry(tmp_path, rows[:2])
    assert validate(tmp_path) == ["unclassified selector: --resource-repro"]

    too_short = entry(
        "--parent-probe",
        project=project,
        classification="runnable",
        arguments=["--parent-probe"],
    )
    write_registry(tmp_path, [rows[0], too_short, rows[2]])
    assert validate(tmp_path) == [
        "registry selector argument count does not match discovered route "
        "(expected at least 3): --parent-probe",
    ]


def test_literal_contains_route_is_discovered_as_a_subset_mode(
    tmp_path: Path,
) -> None:
    selector = "--viewmodel-only"
    write_program(
        tmp_path,
        'if (args.Contains("--viewmodel-only", StringComparer.Ordinal)) return 0;',
    )
    row = entry(
        selector,
        classification="fixture-bound",
        dependencies=["authentic workspace fixture inputs"],
        reason="This explicit viewmodel subset overlaps the fuller default desktop smoke suite.",
    )
    write_registry(tmp_path, [row])
    assert validate(tmp_path) == []

    write_registry(tmp_path, [])
    assert validate(tmp_path) == [f"unclassified selector: {selector}"]


def test_constant_contains_route_is_discovered_as_a_subset_mode(
    tmp_path: Path,
) -> None:
    selector = "--viewmodel-only"
    write_program(
        tmp_path,
        'private const string Selector_ViewmodelOnly = "--viewmodel-only";\n'
        'if (args.Contains(Selector_ViewmodelOnly, StringComparer.Ordinal)) return 0;',
    )
    row = entry(
        selector,
        classification="fixture-bound",
        dependencies=["authentic workspace fixture inputs"],
        reason="This explicit viewmodel subset overlaps the fuller default desktop smoke suite.",
    )
    write_registry(tmp_path, [row])
    assert validate(tmp_path) == []

    write_registry(tmp_path, [])
    assert validate(tmp_path) == [f"unclassified selector: {selector}"]


def test_child_selector_invocation_is_not_an_entrypoint_route(
    tmp_path: Path,
) -> None:
    project = "tests/NpcManager.Architecture.Tests/NpcManager.Architecture.Tests.csproj"
    write_program(tmp_path, 'if (args is ["--test-real-entrypoint"]) return 0;')
    child = tmp_path / Path(project).parent / "ChildRunner.cs"
    child.parent.mkdir(parents=True, exist_ok=True)
    child.write_text(
        'if (args is ["--test-child-probe"]) return 0;', encoding="utf-8")
    write_registry(tmp_path, [entry("--test-real-entrypoint")])
    assert validate(tmp_path) == []


def test_discovery_ignores_noncode_route_shaped_text(tmp_path: Path) -> None:
    write_program(
        tmp_path,
        r'''
if (otherargs is ["--test-other-identifier"]) return 0;
// if (args is ["--test-line-comment"]) return 0;
/* if (args is ["--test-block-comment"]) return 0; */
var normal = "if (args is [\"--test-normal-string\"]) return 0;";
var verbatim = @"if (args is [""--test-verbatim-string""]) return 0;";
var dollarVerbatim = $@"if (args is [""--test-dollar-verbatim-string""]) return 0;";
var verbatimDollar = @$"if (args is [""--test-verbatim-dollar-string""]) return 0;";
var emptyDollarVerbatim = $@"";
var emptyVerbatimDollar = @$"";
var raw = """if (args is ["--test-raw-string"]) return 0;""";
if (args is ["--test-real"]) return 0;
''',
    )
    write_registry(tmp_path, [entry("--test-real")])
    assert validate(tmp_path) == []
