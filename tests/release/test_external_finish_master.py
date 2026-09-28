"""External Skyrim Finish-master admission and public/historical probe behavior."""
import hashlib
import importlib.util
import json
import struct
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]


def verifier():
    spec = importlib.util.spec_from_file_location(
        'external_finish_master_verifier',
        ROOT / 'tools/release/verify_release.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _record(signature, form_id, payload, flags=0):
    return (
        signature.encode('ascii')
        + struct.pack('<III', len(payload), flags, form_id)
        + bytes(8)
        + payload
    )


def _group(records, label=b'PACK', group_type=0):
    body = b''.join(records)
    return (
        b'GRUP'
        + struct.pack('<I', 24 + len(body))
        + label
        + struct.pack('<i', group_type)
        + bytes(8)
        + body
    )


def test_selective_master_scan_skips_compressed_unrelated_records():
    module = verifier()
    compressed_cell = _record('CELL', 0x100, b'opaque-compressed', 0x00040000)
    target = _record('PACK', 0x0001B217, b'x' * 508)
    parsed = module.parse_plugin(
        compressed_cell + _group([target]),
        target_record=('PACK', 0x0001B217),
    )
    assert len(parsed.records) == 1
    assert parsed.records[0].signature == 'PACK'
    assert parsed.records[0].form_id == 0x0001B217
    assert parsed.records[0].groups == ((0, b'PACK'),)
    assert parsed.records[0].raw == target


def test_external_pack_admission_binds_group_length_and_digest(monkeypatch):
    module = verifier()
    target = _record('PACK', 0x0001B217, b'x' * 508)
    expected = hashlib.sha256(b'PACK:00000000\n' + target).hexdigest().upper()
    monkeypatch.setattr(module, 'PUBLIC_FINISH_TEMPLATE_GROUP_BOUND_SHA256', expected)

    record, group_digest = module._extract_canonical_finish_pack_record(
        _group([target]))

    assert record == target
    assert group_digest == expected.lower()


def test_public_master_append_preserves_tes4_extent_and_adds_pack_group(tmp_path, monkeypatch):
    module = verifier()
    base_path = (
        module.PUBLIC_CURRENT_STAGING_PROBE_ROOT
        / 'inputs' / 'A3921E8E17ACEA1BE3141E7F4B2FEA09E9A33444555D2150D00157C84D336979.bin'
    )
    base = base_path.read_bytes()
    target = _record('PACK', 0x0001B217, b'x' * 508)
    expected = hashlib.sha256(b'PACK:00000000\n' + target).hexdigest().upper()
    monkeypatch.setattr(module, 'PUBLIC_FINISH_TEMPLATE_GROUP_BOUND_SHA256', expected)
    pack_record, _ = module._extract_canonical_finish_pack_record(_group([target]))
    destination = tmp_path / 'authority' / 'Skyrim.esm'
    destination.parent.mkdir()

    module._append_public_finish_master(base_path, destination, pack_record)
    result = destination.read_bytes()
    tes4_end = 24 + struct.unpack_from('<I', base, 4)[0]

    assert tes4_end < len(base)
    assert struct.unpack_from('<I', result, 4)[0] == struct.unpack_from('<I', base, 4)[0]
    assert struct.unpack_from('<I', result, 34)[0] == struct.unpack_from('<I', base, 34)[0] + 2
    assert result[:34] == base[:34]
    assert result[38:len(base)] == base[38:]
    assert result[len(base):] == _group([target])
    parsed = module.parse_plugin(result, target_record=('PACK', 0x0001B217))
    assert len(parsed.records) == 1
    assert parsed.records[0].groups == ((0, b'PACK'),)
    assert parsed.records[0].raw == target


@pytest.mark.parametrize('plugin', [
    b'',
    _group([_record('PACK', 0x0001B217, b'x' * 507)]),
    _group([
        _record('PACK', 0x0001B217, b'x' * 508),
        _record('PACK', 0x0001B217, b'y' * 508),
    ]),
    _group([
        _record('PACK', 0x0001B217, b'x' * 508),
        _record('PACK', 0x0101B217, b'y' * 508),
    ]),
    _group([_record('PACK', 0x0001B217, b'x' * 508)], label=b'CELL'),
    _group([_record('PACK', 0x0001B217, b'x' * 508)], group_type=1),
])
def test_external_pack_admission_rejects_wrong_record_shape(plugin):
    module = verifier()
    with pytest.raises(module.ReleaseError):
        module._extract_canonical_finish_pack_record(plugin)


def test_public_route_requires_master_before_temporary_workspace(
    monkeypatch, tmp_path,
):
    module = verifier()
    version = '1.0.0-preview.280'
    monkeypatch.setitem(
        module.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS,
        version,
        module.PUBLIC_CURRENT_STAGING_PROBE_ROOT,
    )
    monkeypatch.delenv('ACTORWRIGHT_TEST_SKYRIM_MASTER', raising=False)

    def no_temporary_workspace():
        pytest.fail('temporary workspace was created before master admission')

    monkeypatch.setattr(
        module, '_verified_protocol_temporary_parent', no_temporary_workspace)
    with pytest.raises(module.ReleaseError, match='ACTORWRIGHT_TEST_SKYRIM_MASTER'):
        module.verify_protocol_v2_kernel(
            tmp_path, set(), {'version': version})


def test_missing_historical_fixture_is_explicitly_unavailable(tmp_path):
    module = verifier()
    probes = [{
        'command': 'npc finish verify',
        'fixtures': [{
            'source': 'finish-verify/Skyrim.esm.base64',
            'destination': 'authority/Skyrim.esm',
            'encoding': 'base64',
            'size': 1,
            'sha256': '0' * 64,
        }],
    }]
    with pytest.raises(module.ReleaseError, match='historical-positive-unavailable'):
        module._require_workflow_fixture_sources(
            probes, tmp_path)