import hashlib
import tracemalloc
import zipfile

from tools.hardening import finalize_package, generate_sbom
from tools.release import verify_release
from tools.verification import compatibility_firewall


def test_streaming_hash_helpers_match_sha256_and_length(tmp_path) -> None:
    path = tmp_path / "sample.bin"
    contents = bytes(range(256)) * 4096
    path.write_bytes(contents)
    expected = hashlib.sha256(contents).hexdigest()

    assert finalize_package._file_identity(path) == (len(contents), expected)
    assert finalize_package._sha256_file(path) == expected
    assert generate_sbom._sha256_file(path) == expected
    assert verify_release.digest(path) == expected.upper()
    assert compatibility_firewall._sha256_file(path) == expected
    assert compatibility_firewall._sha256_file_and_length(path) == (
        len(contents),
        expected,
    )
    package_root = tmp_path / "package"
    package_root.mkdir()
    package_member = package_root / "sample.bin"
    package_member.write_bytes(contents)
    assert finalize_package._files(package_root) == [
        {"path": "sample.bin", "bytes": len(contents), "sha256": expected}
    ]
    archive_path = tmp_path / "package.zip"
    archive_sha256 = finalize_package._deterministic_zip(package_root, archive_path)
    with archive_path.open("rb") as stream:
        assert archive_sha256 == hashlib.file_digest(stream, "sha256").hexdigest()
    with zipfile.ZipFile(archive_path) as archive:
        with archive.open("sample.bin") as member:
            assert compatibility_firewall._sha256_stream(member) == expected


def test_firewall_file_identity_keeps_python_allocation_bounded(tmp_path) -> None:
    path = tmp_path / "large.bin"
    block = bytes(range(256)) * 4096
    expected = hashlib.sha256()
    with path.open("wb") as stream:
        for _ in range(64):
            stream.write(block)
            expected.update(block)

    tracemalloc.start()
    try:
        length, actual = compatibility_firewall._sha256_file_and_length(path)
        _current, peak = tracemalloc.get_traced_memory()
    finally:
        tracemalloc.stop()

    print(f"tracemalloc_peak_python_bytes={peak}")
    assert length == 64 * len(block)
    assert actual == expected.hexdigest()
    assert peak < 8 * 1024 * 1024
