from __future__ import annotations

import importlib.util
import struct
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
SANITIZER_PATH = REPOSITORY / "tools" / "compilers" / "caprica" / "sanitize_pex_metadata.py"


def load_sanitizer():
    assert SANITIZER_PATH.is_file(), f"sanitizer is missing: {SANITIZER_PATH}"
    spec = importlib.util.spec_from_file_location("pex_metadata_sanitizer", SANITIZER_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def encode_string(value: str) -> bytes:
    payload = value.encode("utf-8")
    return struct.pack(">H", len(payload)) + payload


def test_sanitizer_rewrites_only_known_skyrim_header_strings() -> None:
    sanitizer = load_sanitizer()
    suffix = b"\x00\x03table\x00\x01payload\xFF"
    original = (
        b"\xFA\x57\xC0\xDE\x03\x02\x00\x01"
        + b"\x00\x00\x00\x00\x6A\x5D\x3F\xEB"
        + encode_string(r"C:\build\NPCM_Manolov_ApplySSE.psc")
        + encode_string("developer-account")
        + encode_string("build-host")
        + suffix
    )
    expected = (
        b"\xFA\x57\xC0\xDE\x03\x02\x00\x01"
        + b"\x00\x00\x00\x00\x6A\x5D\x3F\xEB"
        + encode_string("runtime/skyrimse/Source/Scripts/NPCM_Manolov_ApplySSE.psc")
        + b"\x00\x00\x00\x00"
        + suffix
    )

    sanitized = sanitizer.sanitize_pex_bytes(original, "NPCM_Manolov_ApplySSE.pex")

    assert sanitized == expected
    assert sanitized.endswith(suffix)


def test_sanitizer_rejects_wrong_script_and_truncated_header() -> None:
    sanitizer = load_sanitizer()
    minimal = b"\xFA\x57\xC0\xDE\x03\x02\x00\x01" + b"\x00" * 8

    try:
        sanitizer.sanitize_pex_bytes(minimal, "OtherScript.pex")
    except ValueError:
        pass
    else:
        raise AssertionError("unknown PEX name was accepted")

    try:
        sanitizer.sanitize_pex_bytes(minimal + b"\x00", "NPCM_Manolov_ApplySSE.pex")
    except ValueError:
        pass
    else:
        raise AssertionError("truncated PEX header was accepted")

def test_sanitizer_requires_string_table_count_after_header() -> None:
    sanitizer = load_sanitizer()
    header_without_table_count = (
        b"\xFA\x57\xC0\xDE\x03\x02\x00\x01"
        + b"\x00" * 8
        + encode_string(r"C:\build\NPCM_Manolov_ApplySSE.psc")
        + encode_string("")
        + encode_string("")
    )

    try:
        sanitizer.sanitize_pex_bytes(header_without_table_count, "NPCM_Manolov_ApplySSE.pex")
    except ValueError:
        pass
    else:
        raise AssertionError("PEX without a string-table count was accepted")
