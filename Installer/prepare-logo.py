from __future__ import annotations

from collections import deque
from pathlib import Path

from PIL import Image, ImageDraw


HERE = Path(__file__).resolve().parent
ASSETS = HERE / "assets"
SOURCE = ASSETS / "logo-source.png"
LOGO_PNG = ASSETS / "logo.png"
INSTALLER_ICO = ASSETS / "installer.ico"


def keep_largest_components(mask: Image.Image, count: int) -> Image.Image:
    width, height = mask.size
    pixels = mask.load()
    visited = bytearray(width * height)
    components: list[list[tuple[int, int]]] = []

    for y in range(height):
        for x in range(width):
            offset = y * width + x
            if visited[offset] or pixels[x, y] == 0:
                continue

            visited[offset] = 1
            queue = deque([(x, y)])
            component: list[tuple[int, int]] = []
            while queue:
                current_x, current_y = queue.popleft()
                component.append((current_x, current_y))
                for next_y in range(max(0, current_y - 1), min(height, current_y + 2)):
                    for next_x in range(max(0, current_x - 1), min(width, current_x + 2)):
                        next_offset = next_y * width + next_x
                        if visited[next_offset] or pixels[next_x, next_y] == 0:
                            continue
                        visited[next_offset] = 1
                        queue.append((next_x, next_y))
            components.append(component)

    cleaned = Image.new("L", mask.size, 0)
    cleaned_pixels = cleaned.load()
    for component in sorted(components, key=len, reverse=True)[:count]:
        for x, y in component:
            cleaned_pixels[x, y] = 255
    return cleaned


def extract_supplied_logo(source: Image.Image) -> Image.Image:
    # Use only the largest logo supplied in the lower-right of the source PNG.
    crop = source.crop((870, 485, 1285, 915)).convert("RGB")
    grayscale = crop.convert("L")
    alpha = grayscale.point(lambda value: 255 if value <= 20 else 0)
    alpha = keep_largest_components(alpha, 10)

    bbox = alpha.getbbox()
    if bbox is None:
        raise RuntimeError("Could not isolate the supplied logo.")

    alpha = alpha.crop(bbox)
    mark = Image.new("RGBA", alpha.size, (0, 0, 0, 255))
    mark.putalpha(alpha)
    return mark


def place_on_white_circle(mark: Image.Image, size: int = 384) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    inset = 3
    draw.ellipse(
        (inset, inset, size - inset - 1, size - inset - 1),
        fill=(255, 255, 255, 255),
        outline=(220, 220, 222, 255),
        width=2,
    )

    max_dimension = round(size * 0.70)
    scale = min(max_dimension / mark.width, max_dimension / mark.height)
    resized = mark.resize(
        (round(mark.width * scale), round(mark.height * scale)),
        Image.Resampling.LANCZOS,
    )
    x = (size - resized.width) // 2
    y = (size - resized.height) // 2
    canvas.alpha_composite(resized, (x, y))
    return canvas


def main() -> None:
    source = Image.open(SOURCE).convert("RGB")
    logo = place_on_white_circle(extract_supplied_logo(source))
    logo.save(LOGO_PNG, optimize=True)
    logo.save(
        INSTALLER_ICO,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
