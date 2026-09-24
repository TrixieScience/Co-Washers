"""Packs the built DLL into a release zip, ready to upload to a GitHub release."""
import re
from pathlib import Path
from hashlib import sha256
from zipfile import ZipFile, ZIP_DEFLATED

here = Path(__file__).resolve().parent
repo = here.parent
dll = here / "build" / "DragNWashCoop.dll"
if not dll.is_file():
    raise SystemExit("Build DragNWashCoop.dll first")
version = re.search(r'BepInPlugin\(Guid, "[^"]*", "([^"]+)"\)', (here / "CoopPlugin.cs").read_text()).group(1)
digest = sha256(dll.read_bytes()).hexdigest()
out = here / "artifacts" / f"Co-Washers-{version}.zip"
out.parent.mkdir(parents=True, exist_ok=True)
plugin = "BepInEx/plugins/DragNWashCoop/"   # everything in one folder, so unzipping into the game folder stays tidy
with ZipFile(out, "w", ZIP_DEFLATED) as archive:
    archive.write(dll, plugin + "DragNWashCoop.dll")
    archive.write(repo / "README.md", plugin + "README.md")
    archive.write(repo / "CHANGELOG.md", plugin + "CHANGELOG.md")
    archive.write(repo / "LICENSE", plugin + "LICENSE")
    archive.write(here / "Assets" / "Chewy-LICENSE.txt", plugin + "Chewy-LICENSE.txt")   # the menu font, embedded in the DLL
    for shot in sorted((repo / "docs").glob("*.jpg")):   # the README's screenshots
        archive.write(shot, plugin + f"docs/{shot.name}")
    archive.writestr(plugin + "SHA256.txt", f"{digest}  DragNWashCoop.dll\n")
print(out)
print(f"SHA-256 {digest}")
