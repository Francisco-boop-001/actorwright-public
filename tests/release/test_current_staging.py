"""Preview.280 staging is strict eleven-ready; frozen releases keep their contracts."""
import importlib.util
import hashlib
import json
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
PROBES = ROOT / 'tools/release/protocol-v2-workflow-probes'
WORKFLOWS = {
    'npc assembly preflight': ['urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1'],
    'npc create-from-jslot': ['urn:actorwright:protocol-v2:npc-create-preflight-result:v1',
                            'urn:actorwright:protocol-v2:npc-create-from-jslot-build-result:v1'],
    'npc finish analyze': ['urn:actorwright:protocol-v2:finish-analyze-result:v1'],
    'npc finish apply': ['urn:actorwright:protocol-v2:finish-apply-result:v1'],
    'npc finish verify': ['urn:actorwright:protocol-v2:finish-verify-result:v1'],
    'preset inspect': ['urn:actorwright:protocol-v2:preset-inspect-result:v1'],
    'preview npc': ['urn:actorwright:protocol-v2:npc-visual-preview-result:v1'],
    'workspace preflight': ['urn:actorwright:protocol-v2:workspace-preflight-result:v1'],
}


def verifier():
    spec = importlib.util.spec_from_file_location('task13_verifier', ROOT / 'tools/release/verify_release.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_current_staging_admits_all_eleven_discovered_commands():
    module = verifier()
    probes = module._load_protocol_v2_workflow_probe_catalog(
        set(WORKFLOWS), WORKFLOWS, release_version='1.0.0-preview.280', root=PROBES / 'current-staging')
    assert {probe['command'] for probe in probes} == set(WORKFLOWS)
    assert len(probes) + len(module.PROTOCOL_V2_KERNEL_READY_COMMANDS) == 11


def test_frozen_preview272_keeps_its_eleven_ready_contract():
    module = verifier()
    probes = module._load_protocol_v2_workflow_probe_catalog(
        set(WORKFLOWS), WORKFLOWS, release_version='1.0.0-preview.272', root=module._select_protocol_v2_probe_root('1.0.0-preview.272'))
    assert {probe['command'] for probe in probes} == set(WORKFLOWS)
    assert len(probes) + len(module.PROTOCOL_V2_KERNEL_READY_COMMANDS) == 11


def test_current_staging_refuses_a_regression_to_frozen_eight():
    module = verifier()
    frozen = set(WORKFLOWS) - {'npc finish analyze', 'npc finish apply', 'npc finish verify'}
    with pytest.raises(module.ReleaseError, match='exactly eleven'):
        module._load_protocol_v2_workflow_probe_catalog(
            frozen, WORKFLOWS, release_version='1.0.0-preview.280', root=PROBES / 'current-staging')


def test_preview276_pins_finish_policy_document_schemas():
    expected = {
        'request-legacy': ('input', 'npc.finish-core.request.v1'),
        'request': ('input', 'npc.finish-core.request.v2'),
        'request-external': ('input', 'npc.finish-core.request.v3'),
        'request-policy': ('input', 'npc.finish-core.request.v4'),
        'proposal-legacy': ('output', 'npc.finish-core.proposal.v1'),
        'proposal': ('output', 'npc.finish-core.proposal.v2'),
        'proposal-external': ('output', 'npc.finish-core.proposal.v3'),
        'proposal-policy': ('output', 'npc.finish-core.proposal.v4'),
        'validation': ('output', 'npc.finish-core.validation.v1'),
    }
    analyze = json.loads((
        PROBES / 'current-staging/schemas/npc-finish-analyze.schema.json'
    ).read_text())
    apply = json.loads((
        PROBES / 'current-staging/schemas/npc-finish-apply.schema.json'
    ).read_text())
    assert {
        row['name']: (row['direction'], row['schemaIdentifier'])
        for row in analyze['documentSchemas']
    } == expected
    assert {
        row['name']: (row['direction'], row['schemaIdentifier'])
        for row in apply['documentSchemas']
    } == {
        name: ('input', value[1]) if name.startswith('proposal') else value
        for name, value in expected.items()
    }

    external_request = next(
        row for row in apply['documentSchemas']
        if row['name'] == 'request-external'
    )['jsonSchema']
    authorities = external_request['properties']['authorities']
    assert 'externalHeadParts' in authorities['properties']
    assert 'externalHeadParts' in authorities['required']
    assert 'externalHeadParts' not in external_request['properties']


def test_current_staging_finish_template_uses_policy_schema():
    module = verifier()
    request = module._pinned_json_schema(
        PROBES / 'current-staging/finish-request-template.json',
        module.CURRENT_STAGING_FINISH_POLICY_TEMPLATE_SHA256,
        'current staging Finish policy template',
    )
    assert request['schema'] == 'npc.finish-core.request.v4'
    assert 'combatPolicy' in request
    assert 'externalHeadParts' not in request['authorities']


def test_current_staging_pins_schema_and_fixture_bytes():
    module = verifier()
    legacy_root = PROBES / 'current-staging'
    legacy_probes = module._load_protocol_v2_workflow_probe_catalog(
        set(WORKFLOWS), WORKFLOWS, release_version='1.0.0-preview.280', root=legacy_root)
    missing_sources = {
        row['source']
        for probe in legacy_probes
        for row in probe['fixtures']
        if not (legacy_root / row['source']).is_file()
    }
    assert missing_sources == {
        'inputs/84CB0AB0F8AB831BDD53CDCC59B1F91825499E53010E4C7C227A94602DD0BC0D.bin'
    }
    with pytest.raises(module.ReleaseError, match='historical-positive-unavailable'):
        module._require_workflow_fixture_sources(legacy_probes, legacy_root)

    public_root = PROBES / 'public-current-staging'
    probes = module._load_protocol_v2_workflow_probe_catalog(
        set(WORKFLOWS), WORKFLOWS, release_version='1.0.0-preview.280', root=legacy_root)
    destinations = []
    for probe in probes:
        name = probe['command'].replace(' ', '-') + '.schema.json'
        result = json.loads((legacy_root / 'schemas' / name).read_text())
        envelope = {'outcome': 'succeeded', 'result': result}
        module._validate_current_staging_schema(envelope, probe['command'])
        result['contract']['readiness'] = 'legacy'
        with pytest.raises(module.ReleaseError, match='exact schema export changed'):
            module._validate_current_staging_schema(envelope, probe['command'])
    public_probes = module._load_protocol_v2_workflow_probe_catalog(
        set(WORKFLOWS), WORKFLOWS, release_version='1.0.0-preview.281', root=public_root)
    for probe in public_probes:
        name = probe['command'].replace(' ', '-') + '.schema.json'
        result = json.loads((public_root / 'schemas' / name).read_text())
        module._validate_current_staging_schema(
            {'outcome': 'succeeded', 'result': result}, probe['command'],
            root=public_root)
        for row in probe['fixtures']:
            relative, _ = module._read_protocol_v2_fixture(probe, row, public_root)
            destinations.append(relative)
            with pytest.raises(module.ReleaseError, match='size or hash mismatch'):
                module._read_protocol_v2_fixture(
                    probe, {**row, 'sha256': '0' * 64}, public_root)
    assert len(destinations) == len(set(destinations))


def test_current_schema_adds_plugin_type_without_rewriting_preview272():
    def plugin_type(root):
        export = json.loads((root / 'schemas/npc-create-from-jslot.schema.json').read_text())
        request = next(
            row['jsonSchema'] for row in export['documentSchemas']
            if row['schemaIdentifier'] == 'npc.create-from-jslot.request.v1')
        output = request['$defs']['output']
        return output['properties'].get('pluginType')

    assert plugin_type(PROBES / 'current-staging') == {
        'type': 'string',
        'default': 'esp',
        'enum': ['esp', 'espfe'],
        'description': (
            'Newly allocated NPCs only. Default esp. espfe keeps the .esp file '
            'name and sets the TES4 light flag at creation without compacting '
            'owned FormIDs (0x800..0xFFF).'),
    }
    assert plugin_type(PROBES / 'preview272') is None


def test_current_staging_retained_artifacts_reject_hash_and_path_drift(tmp_path):
    module = verifier()
    workspace = tmp_path / 'workspace'
    workspace.mkdir()
    output = workspace / 'output.json'
    output.write_bytes(b'{}')
    row = {'path': str(output), 'size': 2, 'sha256': hashlib.sha256(b'{}').hexdigest()}
    module._current_staging_file_binding(row, workspace)
    with pytest.raises(module.ReleaseError, match='physical binding mismatch'):
        module._current_staging_file_binding({**row, 'sha256': '0' * 64}, workspace)
    outside = tmp_path / 'outside.json'
    outside.write_bytes(b'{}')
    with pytest.raises(module.ReleaseError):
        module._current_staging_file_binding({**row, 'path': str(workspace / '..' / 'outside.json')}, workspace)


def _staging_result(
    tmp_path, monkeypatch, *,
    version='1.0.0-preview.280',
    source_line='preview.280-private',
    current_staging=True,
):
    # Exercise the real package manifest/inventory admission; only executable launch is intercepted.
    from test_release_layout import AUTHENTIC_COMMAND_NAMES, _package
    module = verifier()
    package = _package(tmp_path)
    manifest_path = package / 'manifest.json'
    manifest = json.loads(manifest_path.read_text())
    capabilities_path = package / 'capabilities.json'
    capabilities = json.loads(capabilities_path.read_text())
    for value in (manifest, capabilities):
        value.update(version=version, sourceLine=source_line)
    if version in {'1.0.0-preview.273', '1.0.0-preview.274', '1.0.0-preview.275', '1.0.0-preview.276', '1.0.0-preview.277', '1.0.0-preview.278', '1.0.0-preview.279', '1.0.0-preview.280', '1.0.0-preview.281'}:
        manifest['commandCount'] = 142
        capabilities['commands'] = [
            {'name': name} for name in AUTHENTIC_COMMAND_NAMES]
    capabilities_path.write_text(json.dumps(capabilities), encoding='utf-8')
    row = next(row for row in manifest['files'] if row['path'] == 'capabilities.json')
    row.update(size=capabilities_path.stat().st_size, sha256=module.digest(capabilities_path))
    manifest_path.write_text(json.dumps(manifest), encoding='utf-8')
    monkeypatch.setattr(module, 'verify_embedded_resource_closures', lambda *args: None)
    calls = []
    def kernel(root, names, identity, *, current_staging=False):
        calls.append((root, len(names), identity['version'], current_staging))
        return sorted(WORKFLOWS)
    monkeypatch.setattr(module, 'verify_protocol_v2_kernel', kernel)
    result = module.verify_package_staging(package)
    expected_count = 142 if version in {'1.0.0-preview.273', '1.0.0-preview.274', '1.0.0-preview.275', '1.0.0-preview.276', '1.0.0-preview.277', '1.0.0-preview.278', '1.0.0-preview.279', '1.0.0-preview.280', '1.0.0-preview.281'} else 136
    assert calls == [(package, expected_count, version, current_staging)]
    return result


def test_package_staging_selects_current_profile_before_readiness(tmp_path, monkeypatch):
    result = _staging_result(tmp_path, monkeypatch)
    assert result['protocolV2WorkflowCommands'] == sorted(WORKFLOWS)


def test_frozen_preview272_package_staging_keeps_136_command_profile(
    tmp_path, monkeypatch,
):
    _staging_result(
        tmp_path,
        monkeypatch,
        version='1.0.0-preview.272',
        source_line='preview.272-private',
        current_staging=True,
    )


@pytest.mark.skipif(not shutil.which('powershell.exe'), reason='Windows PowerShell required')
@pytest.mark.parametrize('commands', ['current', 'frozen', 'duplicate'])
def test_package_script_checks_exact_current_staging_workflows(tmp_path, monkeypatch, commands):
    result = _staging_result(tmp_path, monkeypatch)
    if commands == 'frozen':
        result['protocolV2WorkflowCommands'] = [
            command for command in result['protocolV2WorkflowCommands']
            if not command.startswith('npc finish ')]
    elif commands == 'duplicate':
        result['protocolV2WorkflowCommands'].append('npc finish verify')
    result_path = tmp_path / 'verification.json'
    result_path.write_text(json.dumps(result), encoding='utf-8')
    source = (ROOT / 'tools/build/package.ps1').read_text(encoding='utf-8-sig')
    start = source.index('$expectedProtocolV2WorkflowCommands =')
    end = source.index('$verificationJson =', start)
    gate = tmp_path / 'package-verification-gate.ps1'
    gate.write_text(
        "param([string]$ResultPath)\n$ErrorActionPreference = 'Stop'\n"
        "$verification = Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json\n"
        + source[start:end], encoding='utf-8')
    completed = subprocess.run(
        [shutil.which('powershell.exe'), '-NoProfile', '-NonInteractive',
         '-ExecutionPolicy', 'Bypass', '-File', str(gate), str(result_path)],
        cwd=ROOT, capture_output=True, text=True, timeout=30)
    assert completed.returncode == (0 if commands == 'current' else 1), completed.stderr
    if commands != 'current':
        assert 'Package verifier did not admit the protocol 2 kernel.' in completed.stderr


@pytest.mark.parametrize('mutation', [
    'authority', 'human-action', 'proposal-path', 'proposal-hash', 'binding-hash',
])
def test_current_staging_refuses_rehashed_final_workflow_review_drift(tmp_path, mutation):
    module = verifier()
    proposal_path = tmp_path / 'proposal.json'
    proposal_path.write_bytes(b'{}')
    proposal = {'kind': 'npc-finish-core-proposal', 'path': str(proposal_path),
                'size': 2, 'sha256': module.digest(proposal_path)}
    workflow = {
        'phase': 'review-required', 'requestDigest': 'A' * 64,
        'artifacts': [proposal], 'authority': module._workflow_authority_rows([proposal]),
        'nextActions': [{
            'command': 'gui', 'requiresHumanAction': True,
            'requiredBindings': [
                {'option': '--proposal', 'value': proposal['path'], 'artifactSha256': proposal['sha256']},
                {'option': '--proposal-sha256', 'value': proposal['sha256'], 'artifactSha256': proposal['sha256']},
            ],
        }],
    }
    workflow_path = tmp_path / 'finish-verified.json'
    artifact = {'kind': 'workflow-bundle', 'path': str(workflow_path),
                'producerCommand': 'npc finish verify', 'requestDigest': 'A' * 64}
    envelope = {'command': 'npc finish verify', 'requestDigest': 'A' * 64,
                'artifacts': [artifact],
                'authority': [{'kind': 'humanVisualAcceptance', 'state': 'required'}]}

    def publish():
        workflow_path.write_text(json.dumps(workflow), encoding='utf-8')
        artifact.update(size=workflow_path.stat().st_size, sha256=module.digest(workflow_path))

    publish()
    module._current_staging_artifacts(envelope, tmp_path, 'review-required')
    action = workflow['nextActions'][0]
    if mutation == 'authority':
        next(row for row in workflow['authority'] if row['kind'] == 'humanVisualAcceptance')['state'] = 'established'
    elif mutation == 'human-action':
        action['requiresHumanAction'] = False
    elif mutation == 'proposal-path':
        action['requiredBindings'][0]['value'] = str(tmp_path / 'different-proposal.json')
    elif mutation == 'proposal-hash':
        action['requiredBindings'][1]['value'] = '0' * 64
    else:
        action['requiredBindings'][0]['artifactSha256'] = '0' * 64
    publish()  # Keep the physical envelope binding valid; only retained workflow semantics drift.
    with pytest.raises(module.ReleaseError, match='authority|human-review'):
        module._current_staging_artifacts(envelope, tmp_path, 'review-required')


def test_preview281_and_preview280_keep_independent_release_pins(tmp_path, monkeypatch):
    module = verifier()
    assert module.EXPECTED_COMMAND_COUNTS_BY_VERSION['1.0.0-preview.281'] == 142
    public_root = PROBES / 'public-current-staging'
    assert module._select_protocol_v2_probe_root('1.0.0-preview.281') == public_root
    public_identity = module._select_binary_release_identity(
        '1.0.0-preview.281', 'preview.281-public')
    assert public_identity['sourceTag'] == 'v1.0.0-preview.281'
    public_package = tmp_path / 'public-preview281'
    public_package.mkdir()
    _staging_result(
        public_package,
        monkeypatch,
        version='1.0.0-preview.281',
        source_line='preview.281-public',
    )

    assert module.EXPECTED_COMMAND_COUNTS_BY_VERSION['1.0.0-preview.280'] == 142
    assert module._select_protocol_v2_probe_root('1.0.0-preview.280') == PROBES / 'current-staging'
    assert module._select_binary_release_identity(
        '1.0.0-preview.280', 'preview.280-private')['sourceTag'] == 'v1.0.0-preview.280'
    assert '1.0.0-preview.280' not in module.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION
    assert module.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION['1.0.0-preview.279'] == 893
    assert module.EXPECTED_COMMAND_COUNTS_BY_VERSION['1.0.0-preview.279'] == 142
    assert module._select_protocol_v2_probe_root('1.0.0-preview.279') == PROBES / 'current-staging'
    assert module._select_binary_release_identity(
        '1.0.0-preview.279', 'preview.279-private')['sourceTag'] == 'v1.0.0-preview.279'
    assert module.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION['1.0.0-preview.278'] == 893
    assert module.EXPECTED_COMMAND_COUNTS_BY_VERSION['1.0.0-preview.278'] == 142
    assert module._select_protocol_v2_probe_root('1.0.0-preview.278') == PROBES / 'current-staging'
    assert module.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION['1.0.0-preview.277'] == 893
    assert module.EXPECTED_COMMAND_COUNTS_BY_VERSION['1.0.0-preview.277'] == 142
    assert module._select_protocol_v2_probe_root('1.0.0-preview.277') == PROBES / 'current-staging'
    assert module.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION['1.0.0-preview.276'] == 880
    assert module.EXPECTED_COMMAND_COUNTS_BY_VERSION['1.0.0-preview.276'] == 142
    assert module._select_protocol_v2_probe_root('1.0.0-preview.276') == PROBES / 'current-staging'
    for preview, name, count in [(275, 'current-staging', 317), (274, 'preview274', 317)]:
        version = f'1.0.0-preview.{preview}'
        identity = module._select_binary_release_identity(version, f'preview.{preview}-private')
        assert identity['sourceTag'] == f'v{version}'
        assert module.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[version] == count
        assert module.EXPECTED_COMMAND_COUNTS_BY_VERSION[version] == 142
        root = module._select_protocol_v2_probe_root(version)
        assert root == PROBES / name
        probes = module._load_protocol_v2_workflow_probe_catalog(
            set(WORKFLOWS), WORKFLOWS, release_version=version, root=root)
        assert {probe['command'] for probe in probes} == set(WORKFLOWS)
        for command in WORKFLOWS:
            schema = json.loads((root / 'schemas' / (command.replace(' ', '-') + '.schema.json')).read_text())
            envelope = {'outcome': 'succeeded', 'result': schema}
            module._validate_current_staging_schema(envelope, command, root=root)
            schema['contract']['readiness'] = 'legacy'
            with pytest.raises(module.ReleaseError, match='exact schema export changed'):
                module._validate_current_staging_schema(envelope, command, root=root)
        with pytest.raises(module.ReleaseError, match='PowerShell wrapper missing'):
            module.verify_release_wrapper({}, version)
        assert module.requires_public_advisory_evidence({'version': version, 'privateOnly': True})
        package_root = tmp_path / name
        package_root.mkdir()
        _staging_result(package_root, monkeypatch, version=version, source_line=f'preview.{preview}-private')
    assert module.PREVIEW274_STAGING_SCHEMA_INVENTORY_SHA256 == 'AA1E590C2EFA6F5595BFADC4056BDAF01AE150B6ECDEFBA6F4D4E36BD973E0AA'
    assert module.CURRENT_STAGING_SCHEMA_INVENTORY_SHA256 == 'A77D5A555EF4234B22CEAECAC9D103555D5835FB0BD4F481FD0ADF6D6BFB5C09'
