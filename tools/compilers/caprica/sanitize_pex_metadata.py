#!/usr/bin/env python3
"""Remove local build identity from the two shipped Skyrim SE PEX headers."""

from __future__ import annotations

import argparse
import hashlib
import os
import tempfile
from pathlib import Path


_SKYRIM_SE_V32_PREFIX = b"\xFA\x57\xC0\xDE\x03\x02\x00\x01"
_HEADER_STRINGS_START = 16  # magic, version, game, and 64-bit compilation time
_SCRIPT_SOURCE = {
    "actorwrightfollowerdialogue.pex": "ActorwrightFollowerDialogue.psc",
    "npcm_manolov_applysse.pex": "NPCM_Manolov_ApplySSE.psc",
}


def _read_string(data: bytes, offset: int) -> tuple[bytes, int]:
    if offset + 2 > len(data):
        raise ValueError("truncated PEX header string length")
    length = int.from_bytes(data[offset : offset + 2], "big")
    start = offset + 2
    end = start + length
    if end > len(data):
        raise ValueError("truncated PEX header string")
    return data[start:end], end


def _encode_string(value: bytes) -> bytes:
    if len(value) > 0xFFFF:
        raise ValueError("PEX header string exceeds the format limit")
    return len(value).to_bytes(2, "big") + value


def sanitize_pex_bytes(data: bytes, pex_filename: str) -> bytes:
    """Replace only the three inline Skyrim header strings; preserve the suffix."""
    filename = pex_filename.replace("\\", "/").rsplit("/", 1)[-1].casefold()
    source_name = _SCRIPT_SOURCE.get(filename)
    if source_name is None:
        raise ValueError("unsupported PEX filename")
    if len(data) < _HEADER_STRINGS_START or data[:8] != _SKYRIM_SE_V32_PREFIX:
        raise ValueError("expected a Skyrim SE PEX v3.2 file")

    offset = _HEADER_STRINGS_START
    source_path, offset = _read_string(data, offset)
    _, offset = _read_string(data, offset)
    _, offset = _read_string(data, offset)
    source_basename = source_path.replace(b"\\", b"/").rsplit(b"/", 1)[-1]
    if source_basename.lower() != source_name.encode("ascii").lower():
        raise ValueError("PEX source basename does not match its filename")
    if offset + 2 > len(data):
        raise ValueError("truncated PEX string-table count")

    portable_source = f"runtime/skyrimse/Source/Scripts/{source_name}".encode("ascii")
    return (
        data[:_HEADER_STRINGS_START]
        + _encode_string(portable_source)
        + b"\x00\x00\x00\x00"
        + data[offset:]
    )


def sanitize_pex_file(path: Path) -> str:
    original = path.read_bytes()
    sanitized = sanitize_pex_bytes(original, path.name)
    if sanitized != original:
        temporary_path: Path | None = None
        try:
            with tempfile.NamedTemporaryFile(
                mode="wb", dir=path.parent, prefix=f".{path.name}.", suffix=".tmp", delete=False
            ) as temporary:
                temporary_path = Path(temporary.name)
                temporary.write(sanitized)
                temporary.flush()
                os.fsync(temporary.fileno())
            os.replace(temporary_path, path)
        finally:
            if temporary_path is not None and temporary_path.exists():
                temporary_path.unlink()
    return hashlib.sha256(sanitized).hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pex", type=Path, help="compiled Actorwright Skyrim SE PEX file")
    args = parser.parse_args()
    try:
        digest = sanitize_pex_file(args.pex)
    except (OSError, ValueError) as exc:
        parser.error(str(exc))
    print(f"PEX_HEADER_SANITIZED sha256={digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())