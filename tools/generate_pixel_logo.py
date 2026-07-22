from io import BytesIO
from pathlib import Path
import struct

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
PNG_PATH = ROOT / "assets" / "Agent-Beacon.png"
ICO_PATH = ROOT / "assets" / "Agent-Beacon.ico"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def pixel_logo() -> Image.Image:
    image = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    outer = [
        (19, 1), (45, 1), (45, 3), (49, 3), (49, 6), (52, 6),
        (52, 50), (49, 50), (49, 53), (39, 53), (39, 56),
        (36, 56), (36, 63), (28, 63), (28, 56), (25, 56),
        (25, 53), (15, 53), (15, 50), (12, 50), (12, 6),
        (15, 6), (15, 3), (19, 3),
    ]
    inner = [
        (21, 4), (43, 4), (43, 6), (47, 6), (47, 9), (49, 9),
        (49, 47), (47, 47), (47, 50), (17, 50), (17, 47),
        (15, 47), (15, 9), (17, 9), (17, 6), (21, 6),
    ]
    draw.polygon(outer, fill=(202, 209, 220, 255))
    draw.polygon(inner, fill=(15, 23, 36, 255))
    draw.rectangle((29, 53, 35, 61), fill=(15, 23, 36, 255))

    def light(top: int, color: tuple[int, int, int, int], highlight: tuple[int, int, int, int]) -> None:
        shape = [
            (27, top), (37, top), (37, top + 2), (41, top + 2),
            (41, top + 4), (43, top + 4), (43, top + 8),
            (41, top + 8), (41, top + 10), (37, top + 10),
            (37, top + 12), (27, top + 12), (27, top + 10),
            (23, top + 10), (23, top + 8), (21, top + 8),
            (21, top + 4), (23, top + 4), (23, top + 2),
            (27, top + 2),
        ]
        draw.polygon(shape, fill=color)
        draw.rectangle((25, top + 3, 28, top + 5), fill=highlight)

    light(7, (230, 45, 59, 255), (255, 91, 103, 255))
    light(21, (240, 177, 0, 255), (255, 216, 77, 255))
    light(35, (16, 157, 88, 255), (64, 210, 132, 255))
    return image


def png_bytes(image: Image.Image) -> bytes:
    buffer = BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def write_ico(base: Image.Image) -> None:
    frames = []
    for size in SIZES:
        frame = base.resize((size, size), Image.Resampling.NEAREST)
        frames.append((size, png_bytes(frame)))

    header_size = 6 + 16 * len(frames)
    offset = header_size
    entries = []
    for size, payload in frames:
        dimension = 0 if size == 256 else size
        entries.append(struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)

    with ICO_PATH.open("wb") as icon_file:
        icon_file.write(struct.pack("<HHH", 0, 1, len(frames)))
        icon_file.write(b"".join(entries))
        for _, payload in frames:
            icon_file.write(payload)


def main() -> None:
    base = pixel_logo()
    preview = base.resize((1024, 1024), Image.Resampling.NEAREST)
    preview.save(PNG_PATH, format="PNG", optimize=True)
    write_ico(base)

    alpha = base.getchannel("A")
    if alpha.getpixel((0, 0)) != 0 or alpha.getpixel((32, 32)) != 255:
        raise RuntimeError("Logo transparency validation failed")
    if len(base.getcolors(maxcolors=256) or []) > 9:
        raise RuntimeError("Logo palette is no longer pixel-art constrained")
    print(f"Wrote {PNG_PATH}")
    print(f"Wrote {ICO_PATH} with sizes: {', '.join(map(str, SIZES))}")


if __name__ == "__main__":
    main()
