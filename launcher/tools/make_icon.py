"""WCW Mayhem icon, cut from the Mayhem arena poster: Goldberg's face on a rounded tile with a
thin edge in the launcher's poster blue. One master image feeds every size, so the launcher and
the game exe share the same icon.

usage (from wcw_launcher):
    python tools/make_icon.py [poster.png] [launcher_assets_dir] [game_ico_path]
defaults: art/background.png, src/WcwLauncher/Assets,
          ../assets/psxrecomp.ico (+ psxrecomp.png beside it)
Writes launcher.ico (256..16 px), launcher-64.png and icon_master.png (1024 px) to the assets
folder, and the same .ico plus a 256 px psxrecomp.png (the runtime's SDL window icon) to the
game project's assets folder."""
import os
import sys
from PIL import Image, ImageDraw, ImageEnhance

here = os.path.dirname(os.path.abspath(__file__))
root = os.path.normpath(os.path.join(here, '..'))
poster = sys.argv[1] if len(sys.argv) > 1 else os.path.join(root, 'art', 'background.png')
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, 'src', 'WcwLauncher', 'Assets')
game_ico = sys.argv[3] if len(sys.argv) > 3 else os.path.normpath(
    os.path.join(root, '..', 'assets', 'psxrecomp.ico'))
os.makedirs(out, exist_ok=True)

S = 1024
EDGE = (37, 91, 168, 255)        # poster blue, darker (accent edge)
EDGE_HI = (77, 144, 240, 255)    # accent end colour

src = Image.open(poster).convert('RGB')
# Square crop on the face: forehead to chin, cheek to cheek (poster is 1672x941).
face = src.crop((262, 0, 742, 480))
face = ImageEnhance.Contrast(face).enhance(1.08)
face = ImageEnhance.Color(face).enhance(1.06)
face = face.resize((S, S), Image.LANCZOS)

radius = int(S * 0.19)
border = int(S * 0.035)

mask = Image.new('L', (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=radius, fill=255)

tile = Image.new('RGBA', (S, S), (0, 0, 0, 0))
# Blue edge: outer rounded rect in the accent, inner one shows the face.
edge = Image.new('RGBA', (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(edge)
d.rounded_rectangle((0, 0, S - 1, S - 1), radius=radius, fill=EDGE)
d.rounded_rectangle((border // 2, border // 2, S - 1 - border // 2, S - 1 - border // 2),
                    radius=radius - border // 2, outline=EDGE_HI, width=max(2, border // 4))
tile.alpha_composite(edge)
inner = Image.new('L', (S, S), 0)
ImageDraw.Draw(inner).rounded_rectangle((border, border, S - 1 - border, S - 1 - border),
                                        radius=radius - border, fill=255)
tile.paste(face, (0, 0), inner)
tile.putalpha(Image.composite(tile.getchannel('A'), Image.new('L', (S, S), 0), mask))

tile.save(os.path.join(out, 'icon_master.png'))
tile.resize((64, 64), Image.LANCZOS).save(os.path.join(out, 'launcher-64.png'))
sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
tile.save(os.path.join(out, 'launcher.ico'), sizes=sizes)
os.makedirs(os.path.dirname(game_ico), exist_ok=True)
tile.save(game_ico, sizes=sizes)
tile.resize((256, 256), Image.LANCZOS).save(os.path.join(os.path.dirname(game_ico), 'psxrecomp.png'))
print('wrote', os.path.join(out, 'launcher.ico'), 'and', game_ico)
