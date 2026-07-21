"""Install XivBlend's companion add-on from a Blender background process.

Usage:
    blender --background --python install_animation_browser.py

The adjacent reviewed add-on and pinned MeddleTools material runtime are copied.
No game data, catalogs, icons, models, textures, or animation clips are installed.
"""

import json
import os
from pathlib import Path
import shutil
import sys
import uuid

import bpy


MODULE_NAME = "xivblend_animation_browser"
SOURCE_FILE = Path(__file__).resolve().with_name(MODULE_NAME) / "__init__.py"
SOURCE_MATERIAL_RUNTIME = Path(__file__).resolve().with_name("MeddleTools")
SOURCE_MATERIAL_LICENSE = Path(__file__).resolve().with_name("MEDDLETOOLS-LICENSE.txt")


def _inside(path, root):
    try:
        return os.path.commonpath((str(path.resolve()), str(root.resolve()))) == str(root.resolve())
    except (OSError, ValueError):
        return False


def install():
    if bpy.app.version < (4, 2, 0):
        raise RuntimeError(
            f"XivBlend requires Blender 4.2 or newer; this is Blender {bpy.app.version_string}."
        )
    if getattr(bpy.app, "factory_startup", False):
        raise RuntimeError(
            "Refusing to install under --factory-startup because saving those preferences "
            "would replace the user's normal Blender preferences. Run with --background only."
        )
    if not SOURCE_FILE.is_file() or SOURCE_FILE.name != "__init__.py":
        raise RuntimeError(f"Known add-on source is missing: {SOURCE_FILE}")
    if not (SOURCE_MATERIAL_RUNTIME / "shaders.blend").is_file():
        raise RuntimeError(
            f"Known material runtime is missing: {SOURCE_MATERIAL_RUNTIME / 'shaders.blend'}"
        )
    if not SOURCE_MATERIAL_LICENSE.is_file():
        raise RuntimeError(f"Known material-runtime license is missing: {SOURCE_MATERIAL_LICENSE}")

    scripts_value = bpy.utils.user_resource("SCRIPTS", create=True)
    if not scripts_value:
        raise RuntimeError("Blender did not provide a user scripts directory")
    scripts_root = Path(scripts_value).resolve()
    addons_root = (scripts_root / "addons").resolve()
    destination_folder = (addons_root / MODULE_NAME).resolve()
    destination_file = (destination_folder / "__init__.py").resolve()
    if not _inside(addons_root, scripts_root):
        raise RuntimeError(f"Unsafe Blender add-ons directory: {addons_root}")
    if not _inside(destination_folder, addons_root) or not _inside(destination_file, destination_folder):
        raise RuntimeError(f"Unsafe add-on destination: {destination_file}")

    destination_folder.mkdir(parents=True, exist_ok=True)

    def copy_reviewed_file(source, destination):
        source = source.resolve()
        destination = destination.resolve()
        if not _inside(source, Path(__file__).resolve().parent):
            raise RuntimeError(f"Unsafe companion source: {source}")
        if not _inside(destination, destination_folder):
            raise RuntimeError(f"Unsafe add-on destination: {destination}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        temporary = destination.parent / f".{destination.name}.{uuid.uuid4().hex}.tmp"
        try:
            shutil.copy2(source, temporary)
            os.replace(temporary, destination)
        finally:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass

    copy_reviewed_file(SOURCE_FILE, destination_file)
    material_destination = destination_folder / "MeddleTools"
    for source in sorted(SOURCE_MATERIAL_RUNTIME.rglob("*")):
        if not source.is_file():
            continue
        relative = source.relative_to(SOURCE_MATERIAL_RUNTIME)
        if "__pycache__" in relative.parts or source.suffix == ".pyc" or source.suffix == ".blend1":
            continue
        copy_reviewed_file(source, material_destination / relative)
    copy_reviewed_file(
        SOURCE_MATERIAL_LICENSE,
        destination_folder / "MEDDLETOOLS-LICENSE.txt",
    )

    # Drop only this module from Python's cache.  The destination directory is
    # deliberately never recursively removed, even during upgrades.
    try:
        bpy.ops.preferences.addon_disable(module=MODULE_NAME)
    except Exception:
        pass
    for module_key in [key for key in sys.modules if key == MODULE_NAME or key.startswith(f"{MODULE_NAME}.")]:
        sys.modules.pop(module_key, None)
    # Blender computes script search paths during startup.  A first-time install
    # creates this directory afterwards, so explicitly refresh it before enable.
    bpy.utils.refresh_script_paths()
    if str(addons_root) not in sys.path:
        sys.path.insert(0, str(addons_root))
    result = bpy.ops.preferences.addon_enable(module=MODULE_NAME)
    if "FINISHED" not in result:
        raise RuntimeError(f"Blender could not enable {MODULE_NAME}: {sorted(result)}")
    save_result = bpy.ops.wm.save_userpref()
    if "FINISHED" not in save_result:
        raise RuntimeError(f"Blender could not save user preferences: {sorted(save_result)}")

    report = {
        "Installed": True,
        "Module": MODULE_NAME,
        "Source": str(SOURCE_FILE),
        "Destination": str(destination_file),
        "MaterialRuntime": str(material_destination),
    }
    print("XIVBLEND_ANIMATION_BROWSER_INSTALL=" + json.dumps(report, ensure_ascii=False, sort_keys=True))
    return report


if __name__ == "__main__":
    install()
