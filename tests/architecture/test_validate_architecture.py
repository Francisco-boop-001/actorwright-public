import importlib.util
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
VALIDATOR = ROOT / "tools" / "architecture" / "validate_architecture.py"


def _validator_module():
    spec = importlib.util.spec_from_file_location(
        "actorwright_architecture_validator", VALIDATOR)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _write(root: Path, relative: str, text: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def _protocol_effect_boundary(extra: str = "") -> str:
    return """
private static ProtocolEffect CreateCore(
    AgentEffectKind kind, string status, string scope) =>
    new ProtocolEffect(kind, status, scope);
private static void Validate(
    AgentEffectKind kind, ApplicationEffectScope scope)
{
    if (!Enum.IsDefined(kind))
        throw new ArgumentOutOfRangeException(nameof(kind));
    if (!ApplicationEffectVocabulary.IsAdmittedPair(kind, scope))
        throw new ArgumentOutOfRangeException(nameof(scope));
}
""" + extra


def _security_classifier_fixture(tmp_path: Path):
    validator = _validator_module()
    registered = sorted(
        validator.SECURITY_CODE_DYNAMIC
        | validator.SECURITY_CODE_PRODUCER_DECLARED
        | {"fixture-output-outside-lab"}
    )
    catalog_rows = "\n".join(
        f'        "{code}",' for code in registered)
    _write(
        tmp_path,
        "src/NpcManager.Cli/DiagnosticExitCodeClassifier.cs",
        """
internal static class DiagnosticExitCodeClassifier
{
    internal static IReadOnlySet<string> RegisteredSecurityCodes =>
        ProtocolDiagnosticClassifier.RegisteredLegacySecurityCodes;
}
""",
    )
    _write(
        tmp_path,
        "src/NpcManager.Application/ProtocolDiagnostics.cs",
        f"""
public static class ProtocolDiagnosticClassifier
{{
    private static readonly FrozenSet<string> SecurityCodes = new[]
    {{
{catalog_rows}
    }}.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> RegisteredLegacySecurityCodes =>
        SecurityCodes;
}}
""",
    )
    _write(
        tmp_path,
        "src/NpcManager.Infrastructure/FixtureProducer.cs",
        '\n'.join([
            'var code = "fixture-output-outside-lab";',
            'var reviewPath = "review-path-outside-lab";',
            'var reviewReparse = "review-reparse-refused";',
        ]),
    )
    return validator


def test_delegating_cli_classifier_uses_application_security_catalog(
    tmp_path: Path,
) -> None:
    validator = _security_classifier_fixture(tmp_path)

    errors: list[str] = []
    validator.validate_cli_exit_classification(tmp_path, errors)
    assert errors == []


def test_bootstrap_security_refusal_must_use_the_shared_classifier(tmp_path: Path) -> None:
    validator = _security_classifier_fixture(tmp_path)
    name = "src/NpcManager.Cli/Program.cs"
    _write(tmp_path, name, "return CommandExitCode.SecurityRefusal;")
    errors: list[str] = []
    validator.validate_cli_exit_classification(tmp_path, errors)
    assert errors == [
        f"{Path(name)}: security refusal must route through DiagnosticExitCodeClassifier"
    ]
    _write(tmp_path, name, "return DiagnosticExitCodeClassifier.KnownSecurityRefusal;")
    errors = []
    validator.validate_cli_exit_classification(tmp_path, errors)
    assert errors == []


def test_reference_preset_cli_tests_have_exact_authorized_references() -> None:
    validator = _validator_module()
    name = "NpcManager.ReferencePreset.Tests.csproj"
    expected = {
        "NpcManager.Application.csproj", "NpcManager.Cli.csproj", "NpcManager.Domain.csproj",
        "NpcManager.FaceGen.csproj", "NpcManager.Formats.Bethesda.csproj", "NpcManager.Infrastructure.csproj",
        "NpcManager.Pipeline.csproj", "NpcManager.Presets.csproj", "NpcManager.Rendering.csproj",
    }
    assert validator.project_references(ROOT / "tests/NpcManager.ReferencePreset.Tests" / name) == expected
    assert validator.EXPECTED[name] == expected


def test_mutable_static_check_distinguishes_readonly_property_from_field() -> None:
    validator = _validator_module()
    errors: list[str] = []

    validator.validate_core_api_source(
        Path("ProtocolDiagnostics.cs"),
        """
public static IReadOnlySet<string> RegisteredLegacySecurityCodes =>
    SecurityCodes;
""",
        errors,
    )
    assert errors == []

    validator.validate_core_api_source(
        Path("Mutable.cs"),
        "public static MutableState Current = new();",
        errors,
    )
    assert errors == [
        "Mutable.cs: possible mutable static state requires review"]


def test_protocol_effect_construction_boundary_is_structural(
    tmp_path: Path,
) -> None:
    validator = _validator_module()
    boundary = "src/NpcManager.Application/ProtocolEffect.cs"
    qualified = "src/NpcManager.Cli/Qualified.cs"
    unchecked = "src/NpcManager.Infrastructure/Unchecked.cs"
    _write(
        tmp_path,
        boundary,
        _protocol_effect_boundary(),
    )
    _write(
        tmp_path,
        qualified,
        """
var effect = new NpcManager.Application.ProtocolEffect(
    kind, status, scope);
""",
    )
    _write(
        tmp_path,
        unchecked,
        """
var effect = ProtocolEffect.CreateUncheckedForTesting(
    kind, status, scope);
""",
    )
    _write(
        tmp_path,
        "src/NpcManager.Cli/Controls.cs",
        """
// new ProtocolEffect(kind, status, scope);
var text = "ProtocolEffect.CreateUncheckedForTesting(kind, status, scope)";
""",
    )

    errors: list[str] = []
    validator.validate_protocol_effect_construction_boundary(tmp_path, errors)

    assert errors == [
        f"{Path(qualified)}: raw ProtocolEffect construction is forbidden",
        f"{Path(unchecked)}: unchecked ProtocolEffect factory is test-only",
    ]

    _write(tmp_path, qualified, "internal sealed class Qualified { }")
    _write(tmp_path, unchecked, "internal sealed class Unchecked { }")
    errors = []
    validator.validate_protocol_effect_construction_boundary(tmp_path, errors)
    assert errors == []

    _write(tmp_path, boundary, "internal sealed class ProtocolEffect { }")
    errors = []
    validator.validate_protocol_effect_construction_boundary(tmp_path, errors)
    assert errors == [
        f"{Path(boundary)}: expected exactly one raw ProtocolEffect "
        "construction, found 0"
    ]


def test_protocol_effect_boundary_rejects_alias_bypasses(
    tmp_path: Path,
) -> None:
    validator = _validator_module()
    boundary = "src/NpcManager.Application/ProtocolEffect.cs"
    alias_raw = "src/NpcManager.Cli/AliasRaw.cs"
    alias_unchecked = "src/NpcManager.Infrastructure/AliasUnchecked.cs"
    _write(
        tmp_path,
        boundary,
        _protocol_effect_boundary(),
    )
    _write(
        tmp_path,
        alias_raw,
        """
using EffectAlias = NpcManager.Application.ProtocolEffect;
var effect = new EffectAlias(kind, status, scope);
""",
    )
    _write(
        tmp_path,
        alias_unchecked,
        """
using EffectAlias = NpcManager.Application.ProtocolEffect;
var effect = EffectAlias.CreateUncheckedForTesting(kind, status, scope);
""",
    )

    errors: list[str] = []
    validator.validate_protocol_effect_construction_boundary(tmp_path, errors)

    assert errors == [
        f"{Path(alias_raw)}: raw ProtocolEffect construction is forbidden",
        f"{Path(alias_unchecked)}: unchecked ProtocolEffect factory is test-only",
    ]


def test_protocol_effect_boundary_rejects_static_import_and_interpolation(
    tmp_path: Path,
) -> None:
    validator = _validator_module()
    boundary = "src/NpcManager.Application/ProtocolEffect.cs"
    static_import = "src/NpcManager.Cli/StaticImport.cs"
    interpolation = "src/NpcManager.Infrastructure/Interpolation.cs"
    _write(
        tmp_path,
        boundary,
        _protocol_effect_boundary(),
    )
    _write(
        tmp_path,
        static_import,
        """
using static NpcManager.Application.ProtocolEffect;
var effect = CreateUncheckedForTesting(kind, status, scope);
""",
    )
    _write(
        tmp_path,
        interpolation,
        r'''
var effect = $"{ProtocolEffect.CreateUncheckedForTesting(
    kind, status, scope)}";
var plain = "ProtocolEffect.CreateUncheckedForTesting(kind, status, scope)";
// CreateUncheckedForTesting(kind, status, scope);
''',
    )

    errors: list[str] = []
    validator.validate_protocol_effect_construction_boundary(tmp_path, errors)

    assert errors == [
        f"{Path(static_import)}: unchecked ProtocolEffect factory is test-only",
        f"{Path(interpolation)}: unchecked ProtocolEffect factory is test-only",
    ]


def test_protocol_effect_boundary_counts_target_typed_construction(
    tmp_path: Path,
) -> None:
    validator = _validator_module()
    boundary = "src/NpcManager.Application/ProtocolEffect.cs"
    _write(
        tmp_path,
        boundary,
        _protocol_effect_boundary("""
private static ProtocolEffect Bypass(
    AgentEffectKind kind, string status, string scope) =>
    new(kind, status, scope);
"""),
    )

    errors: list[str] = []
    validator.validate_protocol_effect_construction_boundary(tmp_path, errors)

    assert errors == [
        f"{Path(boundary)}: expected exactly one raw ProtocolEffect "
        "construction, found 2"
    ]


def test_protocol_effect_boundary_counts_escaped_construction(
    tmp_path: Path,
) -> None:
    validator = _validator_module()
    boundary = "src/NpcManager.Application/ProtocolEffect.cs"
    _write(
        tmp_path,
        boundary,
        _protocol_effect_boundary("""
private static ProtocolEffect Bypass(
    AgentEffectKind kind, string status, string scope) =>
    new @ProtocolEffect(kind, status, scope);
"""),
    )

    errors: list[str] = []
    validator.validate_protocol_effect_construction_boundary(tmp_path, errors)

    assert errors == [
        f"{Path(boundary)}: expected exactly one raw ProtocolEffect "
        "construction, found 2"
    ]


def test_desktop_async_command_sharing_allows_guarded_variant_only(
    tmp_path: Path,
) -> None:
    validator = _validator_module()
    shared = "src/NpcManager.Desktop/AsyncCommand.cs"
    guarded = "src/NpcManager.Desktop/DesktopWorkflowReviewViewModel.cs"
    duplicate = "src/NpcManager.Desktop/ExtraViewModel.cs"
    _write(
        tmp_path,
        shared,
        "internal sealed class AsyncCommand : ICommand { }",
    )
    _write(
        tmp_path,
        guarded,
        """
internal sealed class DesktopWorkflowReviewViewModel
{
    private sealed class AsyncCommand
    {
        private bool executing;
    }
}
""",
    )

    errors: list[str] = []
    validator.validate_desktop_async_command_sharing(tmp_path, errors)
    assert errors == []

    _write(
        tmp_path,
        duplicate,
        """
internal sealed class ExtraViewModel
{
    private sealed class AsyncCommand { }
}
""",
    )
    errors = []
    validator.validate_desktop_async_command_sharing(tmp_path, errors)
    assert errors == [
        f"{Path(duplicate)}: additional AsyncCommand declaration must use "
        "the shared desktop command"
    ]


def test_string_containment_helpers_delegate_except_pinned_path_rules(
    tmp_path: Path,
) -> None:
    validator = _validator_module()
    raw = "src/NpcManager.Pipeline/RawContainment.cs"
    delegated = "src/NpcManager.Pipeline/DelegatedContainment.cs"
    exception = "src/NpcManager.FaceGen/WindowsPinnedPath.cs"
    traversal = "src/NpcManager.BodyGen/BodyGenService.cs"
    _write(
        tmp_path,
        raw,
        '''
private static bool IsWithin(string path, string root)
{
    // Path.GetRelativePath in comments is not code.
    var relative = Path.GetRelativePath(root, path);
    return relative == ".";
}
''',
    )
    _write(
        tmp_path,
        delegated,
        '''
private static bool IsUnder(string path, string root) =>
    new WorkspacePath(path).IsUnder(new WorkspacePath(root));
''',
    )
    _write(
        tmp_path,
        exception,
        '''
public static bool IsSameOrUnder(string path, string root)
{
    // Pinned native paths keep their path-specific canonicalization.
    var relative = Path.GetRelativePath(root, path);
    return !Path.IsPathRooted(relative);
}
''',
    )
    _write(
        tmp_path,
        traversal,
        '''
private static bool HasReparsePointInExistingPath(string root, string target)
{
    string relative = Path.GetRelativePath(root, target);
    return relative.Split(Path.DirectorySeparatorChar).Any();
}
''',
    )

    errors: list[str] = []
    validator.validate_string_containment_helpers(tmp_path, errors)

    assert errors == [
        f"{Path(raw)}: string containment helper must delegate to "
        "WorkspacePath.IsUnder"
    ]


def test_file_path_image_bindings_require_safe_converter(
    tmp_path: Path,
) -> None:
    validator = _validator_module()
    xaml = "src/NpcManager.Desktop/Picker.xaml"
    _write(
        tmp_path,
        xaml,
        '''
<Image Source="{Binding PreviewImagePath}" />
<Image Source="{Binding Path=PreviewImagePath}" />
<Image Source="{Binding PreviewImagePath, Converter={StaticResource PathToImageSource}}" />
<Image Source="{Binding Path=PreviewImagePath, Converter={StaticResource PathToImageSource}}" />
<Image Source="{Binding PreviewImage}" />
''',
    )

    errors: list[str] = []
    validator.validate_desktop_image_path_bindings(tmp_path, errors)

    assert errors == [
        f"{Path(xaml)}: file-path Image.Source bindings must use "
        "PathToImageSource",
        f"{Path(xaml)}: file-path Image.Source bindings must use "
        "PathToImageSource",
    ]
