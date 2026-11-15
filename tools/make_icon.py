"""Draw Nava's native vector-style mascot at Windows icon sizes."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
scale = 4
im = Image.new("RGBA", (256 * scale, 256 * scale), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
def box(values): return tuple(round(v * scale) for v in values)
d.rounded_rectangle(box((4, 4, 252, 252)), radius=58*scale, fill="#1B242B")
d.rounded_rectangle(box((48, 39, 208, 211)), radius=76*scale, fill="#BDE8CB")
d.rounded_rectangle(box((48, 124, 208, 216)), radius=25*scale, fill="#BDE8CB")
for x in (48, 99, 150): d.ellipse(box((x, 182, x+58, 234)), fill="#BDE8CB")
d.rounded_rectangle(box((91, 111, 105, 137)), radius=7*scale, fill="#263C34")
d.rounded_rectangle(box((151, 111, 165, 137)), radius=7*scale, fill="#263C34")
d.arc(box((113, 139, 145, 161)), 5, 175, fill="#44664F", width=5*scale)
d.ellipse(box((70, 147, 92, 159)), fill="#F2AE9D")
d.ellipse(box((164, 147, 186, 159)), fill="#F2AE9D")
im.resize((256, 256), Image.Resampling.LANCZOS).save(root / "nava.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print("Native application icon generated.")
