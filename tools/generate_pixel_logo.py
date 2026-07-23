from io import BytesIO
from pathlib import Path
import struct

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
PNG_PATH = ROOT / "assets" / "Agent-Beacon.png"
ICO_PATH = ROOT / "assets" / "Agent-Beacon.ico"
SVG_PATH = ROOT / "assets" / "Agent-Beacon.svg"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
VIEWBOX = 64

WHITE = (255, 255, 255, 255)
INK = (25, 25, 25, 255)
RED = (216, 74, 47, 255)
YELLOW = (227, 195, 99, 255)
GREEN = (106, 172, 97, 255)

OUTER_FRAME = (17, 6, 46, 57)
INNER_WELL = (23, 12, 40, 51)
LIGHTS = (
    ((28, 16, 35, 23), RED),
    ((28, 28, 35, 35), YELLOW),
    ((28, 40, 35, 47), GREEN),
)


def scaled_box(rectangle: tuple[int, int, int, int], size: int) -> tuple[int, int, int, int]:
    left, top, right, bottom = rectangle
    x1 = round(left * size / VIEWBOX)
    y1 = round(top * size / VIEWBOX)
    x2 = max(x1, round((right + 1) * size / VIEWBOX) - 1)
    y2 = max(y1, round((bottom + 1) * size / VIEWBOX) - 1)
    return x1, y1, x2, y2


def draw_direct(size: int) -> Image.Image:
    image = Image.new("RGBA", (size, size), WHITE)
    draw = ImageDraw.Draw(image)

    draw.rectangle(scaled_box(OUTER_FRAME, size), fill=INK)
    draw.rectangle(scaled_box(INNER_WELL, size), fill=WHITE)
    for rectangle, color in LIGHTS:
        draw.rectangle(scaled_box(rectangle, size), fill=color)
    return image


def logo(size: int) -> Image.Image:
    if size <= 64:
        return draw_direct(64).resize((size, size), Image.Resampling.NEAREST)
    scale = 4
    return draw_direct(size * scale).resize((size, size), Image.Resampling.LANCZOS)


def png_bytes(image: Image.Image) -> bytes:
    buffer = BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def write_ico() -> None:
    frames = [(size, png_bytes(logo(size))) for size in SIZES]
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


def write_svg() -> None:
    outer_left, outer_top, outer_right, outer_bottom = OUTER_FRAME
    inner_left, inner_top, inner_right, inner_bottom = INNER_WELL
    lights = "\n".join(
        f'  <rect x="{left}" y="{top}" width="{right - left + 1}" height="{bottom - top + 1}" fill="#{color[0]:02X}{color[1]:02X}{color[2]:02X}"/>'
        for (left, top, right, bottom), color in LIGHTS
    )
    svg = f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {VIEWBOX} {VIEWBOX}" role="img" aria-labelledby="title description">
  <title id="title">Agent Beacon</title>
  <desc id="description">Centered black rectangular traffic-light frame with three centered square status blocks on an opaque white background.</desc>
  <rect width="{VIEWBOX}" height="{VIEWBOX}" fill="#FFFFFF"/>
  <rect x="{outer_left}" y="{outer_top}" width="{outer_right - outer_left + 1}" height="{outer_bottom - outer_top + 1}" fill="#191919"/>
  <rect x="{inner_left}" y="{inner_top}" width="{inner_right - inner_left + 1}" height="{inner_bottom - inner_top + 1}" fill="#FFFFFF"/>
{lights}
</svg>
"""
    SVG_PATH.write_text(svg, encoding="utf-8", newline="\n")


def validate() -> None:
    preview = logo(1024)
    if preview.getchannel("A").getextrema() != (255, 255):
        raise RuntimeError("Logo background must remain fully opaque")
    if preview.getpixel((0, 0)) != WHITE:
        raise RuntimeError("Logo canvas corners must remain white")

    tiny = logo(16)
    pixels = list(tiny.get_flattened_data())
    checks = (
        ("red", lambda p: p[0] > p[1] + 30 and p[0] > p[2] + 30),
        ("yellow", lambda p: p[0] > 150 and p[1] > 80 and p[1] > p[2] + 30),
        ("green", lambda p: p[1] > p[0] + 30 and p[1] > p[2] + 20),
    )
    for name, predicate in checks:
        if not any(predicate(pixel) for pixel in pixels):
            raise RuntimeError(f"{name} status light is not readable at 16px")


def main() -> None:
    validate()
    logo(1024).save(PNG_PATH, format="PNG", optimize=True)
    write_ico()
    write_svg()
    print(f"Wrote {PNG_PATH}")
    print(f"Wrote {SVG_PATH}")
    print(f"Wrote {ICO_PATH} with sizes: {', '.join(map(str, SIZES))}")


if __name__ == "__main__":
    main()
