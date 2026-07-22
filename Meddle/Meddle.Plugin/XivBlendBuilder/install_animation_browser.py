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
MANAGED_MARKER = ".xivblend-managed.json"
SOURCE_FILE = Path(__file__).resolve().with_name(MODULE_NAME) / "__init__.py"
SOURCE_MATERIAL_RUNTIME = Path(__file__).resolve().with_name("MeddleTools")
SOURCE_MATERIAL_LICENSE = Path(__file__).resolve().with_name("MEDDLETOOLS-LICENSE.txt")


def _inside(path, root):
    try:
        return os.path.commonpath((str(path.resolve()), str(root.resolve()))) == str(root.resolve())
    except (OSError, ValueError):
        return False


def _direct_child(path, parent):
    try:
        return path.parent.resolve() == parent.resolve()
    except OSError:
        return False


def _remove_managed_tree(
    path,
    addons_root,
    *,
    expected_name=None,
    generated_prefix=None,
    require_marker=False,
):
    """Remove only an exact add-on directory that this installer owns."""
    path = Path(path)
    if not path.exists() and not path.is_symlink():
        return
    if not _direct_child(path, addons_root):
        raise RuntimeError(
            f"Refusing to remove a path outside the Blender add-ons directory: {path}"
        )
    if expected_name is not None and path.name != expected_name:
        raise RuntimeError(f"Refusing to remove an unexpected add-on directory: {path}")
    if generated_prefix is not None and not path.name.startswith(generated_prefix):
        raise RuntimeError(f"Refusing to remove an unexpected installer directory: {path}")
    if require_marker and not (path / MANAGED_MARKER).is_file():
        raise RuntimeError(f"Refusing to remove an unmarked add-on directory: {path}")

    if path.is_symlink() or path.is_file():
        path.unlink()
    else:
        shutil.rmtree(path)


def _purge_module_cache():
    for module_key in [
        key for key in sys.modules
        if key == MODULE_NAME or key.startswith(f"{MODULE_NAME}.")
    ]:
        sys.modules.pop(module_key, None)


def install():
    if bpy.app.version < (5, 0, 0) or bpy.app.version >= (6, 0, 0):
        raise RuntimeError(
            f"XivBlend requires Blender 5.x; this is Blender {bpy.app.version_string}."
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
    destination_folder = addons_root / MODULE_NAME
    destination_file = destination_folder / "__init__.py"
    if not _inside(addons_root, scripts_root):
        raise RuntimeError(f"Unsafe Blender add-ons directory: {addons_root}")
    addons_root.mkdir(parents=True, exist_ok=True)
    if not _direct_child(destination_folder, addons_root):
        raise RuntimeError(f"Unsafe add-on destination: {destination_file}")
    if (destination_folder.exists() or destination_folder.is_symlink()) and not _inside(
        destination_folder, addons_root
    ):
        raise RuntimeError(
            f"The existing add-on destination is an unsafe filesystem link: {destination_folder}"
        )

    operation_id = uuid.uuid4().hex
    staging_folder = addons_root / f".{MODULE_NAME}.{operation_id}.staging"
    backup_folder = addons_root / f".{MODULE_NAME}.{operation_id}.backup"
    if not _direct_child(staging_folder, addons_root) or not _direct_child(
        backup_folder, addons_root
    ):
        raise RuntimeError("Unsafe temporary add-on update directory")
    staging_folder.mkdir(parents=False, exist_ok=False)
    (staging_folder / MANAGED_MARKER).write_text(
        json.dumps({"Module": MODULE_NAME, "InstallerSchema": 1}, sort_keys=True),
        encoding="utf-8",
    )

    def copy_reviewed_file(source, destination):
        source = source.resolve()
        destination = Path(destination)
        if not _inside(source, Path(__file__).resolve().parent):
            raise RuntimeError(f"Unsafe companion source: {source}")
        if not _inside(destination, staging_folder):
            raise RuntimeError(f"Unsafe add-on destination: {destination}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)

    try:
        copy_reviewed_file(SOURCE_FILE, staging_folder / "__init__.py")
        staging_materials = staging_folder / "MeddleTools"
        for source in sorted(SOURCE_MATERIAL_RUNTIME.rglob("*")):
            if not source.is_file():
                continue
            relative = source.relative_to(SOURCE_MATERIAL_RUNTIME)
            if (
                "__pycache__" in relative.parts
                or source.suffix == ".pyc"
                or source.suffix == ".blend1"
            ):
                continue
            copy_reviewed_file(source, staging_materials / relative)
        copy_reviewed_file(
            SOURCE_MATERIAL_LICENSE,
            staging_folder / "MEDDLETOOLS-LICENSE.txt",
        )

        was_enabled = MODULE_NAME in bpy.context.preferences.addons
        try:
            bpy.ops.preferences.addon_disable(module=MODULE_NAME)
        except Exception:
            pass
        _purge_module_cache()

        backup_created = False
        installed_new_copy = False
        try:
            if destination_folder.exists() or destination_folder.is_symlink():
                os.replace(destination_folder, backup_folder)
                backup_created = True
            os.replace(staging_folder, destination_folder)
            installed_new_copy = True

            # Blender computes script search paths during startup. A first-time
            # install creates this directory afterwards, so refresh before enable.
            bpy.utils.refresh_script_paths()
            if str(addons_root) not in sys.path:
                sys.path.insert(0, str(addons_root))
            result = bpy.ops.preferences.addon_enable(module=MODULE_NAME)
            if "FINISHED" not in result:
                raise RuntimeError(f"Blender could not enable {MODULE_NAME}: {sorted(result)}")
            save_result = bpy.ops.wm.save_userpref()
            if "FINISHED" not in save_result:
                raise RuntimeError(f"Blender could not save user preferences: {sorted(save_result)}")
        except Exception:
            try:
                bpy.ops.preferences.addon_disable(module=MODULE_NAME)
            except Exception:
                pass
            _purge_module_cache()
            if installed_new_copy:
                _remove_managed_tree(
                    destination_folder,
                    addons_root,
                    expected_name=MODULE_NAME,
                    require_marker=True,
                )
            if backup_created:
                os.replace(backup_folder, destination_folder)
                bpy.utils.refresh_script_paths()
                if was_enabled:
                    try:
                        bpy.ops.preferences.addon_enable(module=MODULE_NAME)
                    except Exception:
                        pass
            raise

        if backup_created:
            try:
                _remove_managed_tree(
                    backup_folder,
                    addons_root,
                    generated_prefix=f".{MODULE_NAME}.{operation_id}.backup",
                )
            except OSError:
                # The active installation is complete. A locked, hidden backup
                # is not imported by Blender and can be removed on the next run.
                pass
    finally:
        if staging_folder.exists() or staging_folder.is_symlink():
            _remove_managed_tree(
                staging_folder,
                addons_root,
                generated_prefix=f".{MODULE_NAME}.{operation_id}.staging",
                require_marker=True,
            )

    material_destination = destination_folder / "MeddleTools"

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
