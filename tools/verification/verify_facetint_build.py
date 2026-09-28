"""Independent oracle for the semantic FaceTint build artifact."""

import argparse
import hashlib
import json
import math
import struct
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def compose(manifest):
    result = list(manifest.get("baseColor", [0.0, 0.0, 0.0, 1.0]))
    for layer in manifest.get("layers", []):
        source = layer["color"]
        opacity = layer["opacity"] * source[3]
        blend = layer.get("blend", "over").lower()
        if blend == "multiply":
            rgb = [result[i] * source[i] for i in range(3)]
        elif blend == "add":
            rgb = [min(1.0, result[i] + source[i]) for i in range(3)]
        else:
            rgb = source[:3]
        if blend == "replace":
            result[:3] = rgb
            result[3] = source[3]
        else:
            for i in range(3):
                result[i] = rgb[i] * opacity + result[i] * (1.0 - opacity)
            result[3] = opacity + result[3] * (1.0 - opacity)
    if manifest.get("alphaMode", "preserve").lower() == "opaque":
        result[3] = 1.0
    return result


def quantize(value):
    return max(0, min(255, int(round(value * 255.0))))


def decode_bc3(data, width, height):
    """Decode the single-mip DXT5 payload used by the provider fixture."""
    blocks_w = (width + 3) // 4
    blocks_h = (height + 3) // 4
    expected = 128 + blocks_w * blocks_h * 16
    assert len(data) == expected and data[84:88] == b"DXT5", "Provider DDS is not the bounded BC3 source format"
    pixels = bytearray(width * height * 4)

    def expand565(value):
        return ((value >> 11 & 0x1F) << 3 | (value >> 11 & 0x1F) >> 2,
                ((value >> 5 & 0x3F) << 2) | (value >> 5 & 0x3F) >> 4,
                (value & 0x1F) << 3 | (value & 0x1F) >> 2)

    def palette(c0, c1):
        first, second = expand565(c0), expand565(c1)
        colors = [first, second]
        if c0 > c1:
            colors.append(tuple((2 * first[i] + second[i]) // 3 for i in range(3)))
            colors.append(tuple((first[i] + 2 * second[i]) // 3 for i in range(3)))
        else:
            colors.append(tuple((first[i] + second[i]) // 2 for i in range(3)))
            colors.append((0, 0, 0))
        return colors

    def alpha_palette(a0, a1):
        values = [a0, a1]
        if a0 > a1:
            values.extend((6 * a0 + a1) // 7 for _ in [0])
            values.extend((5 * a0 + 2 * a1) // 7 for _ in [0])
            values.extend((4 * a0 + 3 * a1) // 7 for _ in [0])
            values.extend((3 * a0 + 4 * a1) // 7 for _ in [0])
            values.extend((2 * a0 + 5 * a1) // 7 for _ in [0])
            values.extend((a0 + 6 * a1) // 7 for _ in [0])
        else:
            values.extend((4 * a0 + a1) // 5 for _ in [0])
            values.extend((3 * a0 + 2 * a1) // 5 for _ in [0])
            values.extend((2 * a0 + 3 * a1) // 5 for _ in [0])
            values.extend((a0 + 4 * a1) // 5 for _ in [0])
            values.extend((0, 255))
        return values

    offset = 128
    for block_y in range(blocks_h):
        for block_x in range(blocks_w):
            block = data[offset:offset + 16]
            offset += 16
            alphas = alpha_palette(block[0], block[1])
            alpha_bits = int.from_bytes(block[2:8], "little")
            c0, c1 = struct.unpack_from("<HH", block, 8)
            colors = palette(c0, c1)
            color_bits = struct.unpack_from("<I", block, 12)[0]
            for row in range(4):
                for column in range(4):
                    x, y = block_x * 4 + column, block_y * 4 + row
                    if x >= width or y >= height:
                        continue
                    color_index = (color_bits >> (2 * (row * 4 + column))) & 3
                    alpha_index = (alpha_bits >> (3 * (row * 4 + column))) & 7
                    red, green, blue = colors[color_index]
                    pixel = (y * width + x) * 4
                    pixels[pixel:pixel + 4] = bytes((blue, green, red, alphas[alpha_index]))
    return pixels


def decode_provider_dds(data, width, height):
    if data[84:88] == b"DXT5":
        return decode_bc3(data, width, height)
    assert len(data) == 128 + width * height * 4, "Provider DDS is not a bounded BGRA8 or BC3 raster"
    assert data[80:84] == struct.pack("<I", 0x41) and data[88:108] == struct.pack(
        "<IIIII", 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000), \
        "Provider DDS is not a bounded BGRA8 raster"
    return bytearray(data[128:])


def compose_provider(manifest, provider_root):
    width, height = manifest["width"], manifest["height"]
    pixels = bytearray(4 * width * height)
    base = manifest.get("baseColor", [0.0, 0.0, 0.0, 1.0])
    for offset in range(0, len(pixels), 4):
        pixels[offset:offset + 4] = bytes((quantize(base[2]), quantize(base[1]),
                                            quantize(base[0]), quantize(base[3])))
    for layer in manifest.get("layers", []):
        source_path = (provider_root / Path(layer["source"])).resolve()
        source = decode_provider_dds(source_path.read_bytes(), width, height)
        color = layer["color"]
        opacity_scale = layer["opacity"]
        blend = layer.get("blend", "over").lower()
        for offset in range(0, len(pixels), 4):
            source_blue = source[offset] / 255.0 * color[2]
            source_green = source[offset + 1] / 255.0 * color[1]
            source_red = source[offset + 2] / 255.0 * color[0]
            source_alpha = source[offset + 3] / 255.0 * color[3]
            if blend == "replace":
                pixels[offset:offset + 4] = bytes((quantize(source_blue), quantize(source_green),
                                                   quantize(source_red), quantize(source_alpha)))
                continue
            target_blue, target_green, target_red, target_alpha = (
                pixels[offset] / 255.0, pixels[offset + 1] / 255.0,
                pixels[offset + 2] / 255.0, pixels[offset + 3] / 255.0)
            opacity = opacity_scale * source_alpha
            if blend == "multiply":
                blue, green, red = target_blue * source_blue, target_green * source_green, target_red * source_red
            elif blend == "add":
                blue, green, red = min(1.0, target_blue + source_blue), min(1.0, target_green + source_green), min(1.0, target_red + source_red)
            else:
                blue, green, red = source_blue, source_green, source_red
            pixels[offset] = quantize(blue * opacity + target_blue * (1.0 - opacity))
            pixels[offset + 1] = quantize(green * opacity + target_green * (1.0 - opacity))
            pixels[offset + 2] = quantize(red * opacity + target_red * (1.0 - opacity))
            pixels[offset + 3] = quantize(opacity + target_alpha * (1.0 - opacity))
    if manifest.get("alphaMode", "preserve").lower() == "opaque":
        for offset in range(3, len(pixels), 4):
            pixels[offset] = 255
    return pixels


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--repeat", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--dds", type=Path)
    parser.add_argument("--dds-format", choices=("bgra8", "bc3", "bc7"), default="bgra8")
    parser.add_argument("--provider-root", type=Path)
    args = parser.parse_args()

    manifest = load(args.manifest)
    artifact = load(args.output)
    repeat = load(args.repeat)
    response = load(args.response)
    artifact_semantic = dict(artifact)
    repeat_semantic = dict(repeat)
    for key in ("textureOutputPath", "textureDdsSha256"):
        artifact_semantic.pop(key, None)
        repeat_semantic.pop(key, None)
    assert artifact_semantic == repeat_semantic, "Repeated semantic FaceTint artifact changed"
    assert hashlib.sha256(args.manifest.read_bytes()).hexdigest().casefold() == artifact["inputManifestSha256"].casefold()
    assert artifact["artifactKind"] == "facetint-semantic-build"
    assert artifact["edition"] in ("fallout4", "skyrimse")
    assert artifact.get("rasterSource", "uniform") in ("uniform", "provider-sampled")
    assert artifact["width"] == artifact["height"] == manifest["width"]
    assert artifact["format"] in ("bgra8", "bc3", "bc7") and artifact["mipCount"] == 1
    assert artifact["channelOrder"] == "bgra8"
    assert [item["name"] for item in artifact["orderedLayers"]] == [item["name"] for item in manifest["layers"]]
    if artifact["rasterSource"] == "uniform":
        expected = compose(manifest)
        for probe in artifact["probes"]:
            for key, value in zip(("red", "green", "blue", "alpha"), expected):
                assert math.isclose(probe[key], value, rel_tol=0.0, abs_tol=1e-12), f"probe {key} drifted"
    else:
        for probe in artifact["probes"]:
            assert 0 <= probe["x"] < artifact["width"] and 0 <= probe["y"] < artifact["height"]
    if args.provider_root is not None:
        assert artifact["rasterSource"] == "provider-sampled"
        bindings = artifact.get("providerSources") or []
        assert len(bindings) == len(manifest["layers"]), "Provider binding count drifted"
        for layer, binding in zip(manifest["layers"], bindings):
            assert binding["source"] == layer["source"]
            source = (args.provider_root / Path(layer["source"])).resolve()
            assert source.is_relative_to(args.provider_root.resolve()) and source.suffix.lower() == ".dds"
            source_bytes = source.read_bytes()
            assert hashlib.sha256(source_bytes).hexdigest().casefold() == binding["sha256"].casefold()
            assert source_bytes[:4] == b"DDS " and struct.unpack_from("<I", source_bytes, 4)[0] == 124
            assert struct.unpack_from("<I", source_bytes, 12)[0] == artifact["height"]
            assert struct.unpack_from("<I", source_bytes, 16)[0] == artifact["width"]
            assert binding["width"] == artifact["width"] and binding["height"] == artifact["height"]
        expected_pixels = compose_provider(manifest, args.provider_root)
        expected_hash = hashlib.sha256(expected_pixels).hexdigest()
        assert artifact["semanticRasterSha256"].casefold() == expected_hash.casefold(), \
            "Provider sampled raster semantic hash drifted"
        if args.dds is not None and args.dds_format == "bgra8":
            assert args.dds.read_bytes()[128:] == expected_pixels, \
                "Provider sampled BGRA8 payload drifted from the independent composition"
    else:
        assert artifact["rasterSource"] == "uniform"
    assert response["written"] is True
    assert response["artifactKind"] == "facetint-semantic-build"
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    if args.dds is not None:
        dds = args.dds.read_bytes()
        assert artifact.get("textureOutputPath"), "DDS output path missing from semantic artifact"
        assert artifact.get("textureDdsSha256", "").casefold() == hashlib.sha256(dds).hexdigest().casefold()
        assert dds[:4] == b"DDS " and struct.unpack_from("<I", dds, 4)[0] == 124
        assert struct.unpack_from("<I", dds, 12)[0] == artifact["height"]
        assert struct.unpack_from("<I", dds, 16)[0] == artifact["width"]
        assert artifact["format"] == args.dds_format
        if args.dds_format == "bgra8":
            assert len(dds) == 128 + artifact["width"] * artifact["height"] * 4
        elif args.dds_format == "bc3":
            blocks = ((artifact["width"] + 3) // 4) * ((artifact["height"] + 3) // 4)
            assert len(dds) == 128 + blocks * 16 and dds[84:88] == b"DXT5"
        else:
            blocks = ((artifact["width"] + 3) // 4) * ((artifact["height"] + 3) // 4)
            assert len(dds) == 148 + blocks * 16 and dds[84:88] == b"DX10"
            assert struct.unpack_from("<I", dds, 128)[0] == 98  # DXGI_FORMAT_BC7_UNORM
    print(f"FACETINT BUILD INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
