import os
from pathlib import Path

import pytest


def _require_ntfs_fixture(root: Path, feature: str) -> str:
    strict = os.environ.get("ACTORWRIGHT_REQUIRE_NTFS_FIXTURES") == "1"
    if os.name != "nt":
        message = (
            f"{feature} fixture requires Windows NTFS; root={root}; "
            f"filesystem=non-Windows ({os.name})"
        )
        if strict:
            pytest.fail(message, pytrace=False)
        pytest.skip(message)

    try:
        import ctypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        get_volume_path_name = kernel32.GetVolumePathNameW
        get_volume_path_name.argtypes = (
            ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_wchar), ctypes.c_uint32,
        )
        get_volume_path_name.restype = ctypes.c_int
        volume_root = ctypes.create_unicode_buffer(32768)
        if not get_volume_path_name(
            str(Path(root).resolve()), volume_root, len(volume_root)
        ):
            raise ctypes.WinError(ctypes.get_last_error())

        get_volume_information = kernel32.GetVolumeInformationW
        get_volume_information.argtypes = (
            ctypes.c_wchar_p,
            ctypes.POINTER(ctypes.c_wchar),
            ctypes.c_uint32,
            ctypes.POINTER(ctypes.c_uint32),
            ctypes.POINTER(ctypes.c_uint32),
            ctypes.POINTER(ctypes.c_uint32),
            ctypes.POINTER(ctypes.c_wchar),
            ctypes.c_uint32,
        )
        get_volume_information.restype = ctypes.c_int
        filesystem = ctypes.create_unicode_buffer(64)
        serial = ctypes.c_uint32()
        max_component = ctypes.c_uint32()
        flags = ctypes.c_uint32()
        if not get_volume_information(
            volume_root.value,
            None,
            0,
            ctypes.byref(serial),
            ctypes.byref(max_component),
            ctypes.byref(flags),
            filesystem,
            len(filesystem),
        ):
            raise ctypes.WinError(ctypes.get_last_error())
        filesystem_name = filesystem.value
        if not filesystem_name:
            raise OSError("Windows returned an empty filesystem name")
    except Exception as error:
        pytest.fail(
            f"Could not establish filesystem for {feature} fixture; "
            f"root={root}; filesystem=unknown; reason={error}",
            pytrace=False,
        )

    if filesystem_name.casefold() != "ntfs":
        message = (
            f"{feature} fixture requires NTFS; root={root}; "
            f"filesystem={filesystem_name}"
        )
        if strict:
            pytest.fail(message, pytrace=False)
        pytest.skip(message)
    return filesystem_name


def _report_fixture_creation_failure(
    root: Path, filesystem: str, feature: str, error: BaseException,
) -> None:
    message = (
        f"{feature} fixture creation failed; root={root}; "
        f"filesystem={filesystem}; reason={type(error).__name__}: {error}"
    )
    if not filesystem or filesystem.casefold() == "unknown":
        pytest.fail(message, pytrace=False)
    if (
        filesystem.casefold() == "ntfs"
        or os.environ.get("ACTORWRIGHT_REQUIRE_NTFS_FIXTURES") == "1"
    ):
        pytest.fail(message, pytrace=False)
    pytest.skip(message)


@pytest.fixture
def require_ntfs_fixture():
    return _require_ntfs_fixture


@pytest.fixture
def filesystem_fixture_unavailable():
    return _report_fixture_creation_failure
