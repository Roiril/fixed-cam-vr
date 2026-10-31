"""Derive the unchanged CRT aperture contour from the reference frame alpha."""
from pathlib import Path
from PIL import Image, ImageChops

root = Path(__file__).resolve().parents[2]
alpha = ImageChops.invert(Image.open(root / 'Assets/Resources/CrtReference/Frame.png').getchannel('A'))
# Remove the generated mask's near-opaque casing noise. Preserve its 50% contour
# and a short antialiased transition, without fitting a new rounded rectangle.
alpha = alpha.point(lambda v: 0 if v < 112 else 255 if v > 144 else round((v - 112) * 255 / 32))
mask = Image.new('RGBA', alpha.size, 'white')
mask.putalpha(alpha)
mask.save(Path(__file__).with_name('screen-mask.png'))
