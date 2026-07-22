"""Headless regression test for XivBlend's Blender Render Studio controls.

Run with Blender, not the system Python::

    blender --background --factory-startup --python-exit-code 1 \
      --python tools/test_render_studio.py
"""

from __future__ import annotations

import importlib.util
from pathlib import Path
import sys

import bpy


ROOT = Path(__file__).resolve().parents[1]
ADDON = (
    ROOT
    / "Meddle"
    / "Meddle.Plugin"
    / "XivBlendBuilder"
    / "xivblend_animation_browser"
    / "__init__.py"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def load_addon():
    spec = importlib.util.spec_from_file_location("xivblend_animation_browser", ADDON)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load the animation browser from {ADDON}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class _RestrictedData:
    """Fail loudly if add-on registration tries to inspect the open file."""

    def __init__(self, accesses: list[str]):
        self._accesses = accesses

    def __getattr__(self, name: str):
        self._accesses.append(name)
        raise AssertionError(
            f"register() accessed bpy.data.{name} while Blender data was restricted"
        )


class _RestrictedBpy:
    """Delegate Blender's registration API but expose _RestrictData semantics."""

    def __init__(self, wrapped, accesses: list[str]):
        self._wrapped = wrapped
        self.data = _RestrictedData(accesses)

    def __getattr__(self, name: str):
        return getattr(self._wrapped, name)


def main() -> None:
    if not bpy.app.background or getattr(bpy.app, "factory_startup", False) is False:
        raise RuntimeError("Run this test with --background --factory-startup")

    # Simulate a file saved by the retired clay-preview implementation. Blender
    # exposes bpy.data as _RestrictData while enabling an add-on, so register()
    # must defer this file migration until load_post.
    legacy = bpy.data.materials.new("XivBlend Smooth Animation Preview")
    legacy["xivblend_runtime_preview_material"] = True
    legacy_name = legacy.name
    bpy.context.scene.view_layers[0].material_override = legacy

    sys.dont_write_bytecode = True
    addon = load_addon()
    restricted_accesses: list[str] = []
    addon.bpy = _RestrictedBpy(bpy, restricted_accesses)
    registration_error = None
    try:
        addon.register()
    except Exception as error:
        registration_error = error
    finally:
        addon.bpy = bpy

    if registration_error is not None:
        # register() adds classes before its final setup steps. Make a failed
        # test repeatable in the same Blender process by undoing partial state.
        try:
            addon.unregister()
        except Exception:
            pass
        raise RuntimeError(
            f"add-on registration failed in _RestrictData context: {registration_error}"
        ) from registration_error

    try:
        scene = bpy.context.scene
        require(not restricted_accesses, f"register inspected bpy.data: {restricted_accesses}")
        require(
            scene.view_layers[0].material_override is legacy,
            "legacy file migration ran during restricted registration",
        )
        require(
            bpy.data.materials.get(legacy_name) is legacy,
            "legacy material was removed during restricted registration",
        )

        addon._load_post_handler(None)
        require(scene.view_layers[0].material_override is None, "legacy clay override survived")
        require(bpy.data.materials.get(legacy_name) is None, "legacy clay material survived")

        scene.xivblend_render_quality = "ANIMATE"
        require(scene.render.engine == "BLENDER_EEVEE", "Animate did not retain Eevee renders")

        scene.xivblend_render_quality = "BEAUTY"
        require(scene.render.engine == "CYCLES", "Beauty did not select Cycles")
        require(scene.cycles.samples == 256, "Beauty sample count changed")
        require(scene.cycles.adaptive_min_samples == 16, "Beauty minimum samples changed")
        require(scene.cycles.texture_limit == "2048", "viewport texture limit changed")

        scene.xivblend_color_preset = "ACCURATE"
        require(
            scene.view_settings.view_transform == "Khronos PBR Neutral",
            "accurate-color transform changed",
        )

        scene.xivblend_background_preset = "TRANSPARENT"
        require(scene.render.film_transparent, "transparent background did not enable film alpha")

        scene.xivblend_output_preset = "EXR"
        require(scene.render.image_settings.file_format == "OPEN_EXR", "EXR output changed")
        require(scene.render.image_settings.color_depth == "16", "EXR depth changed")
    finally:
        addon.unregister()

    require(
        not hasattr(bpy.types.Scene, "xivblend_render_quality"),
        "add-on properties survived unregister",
    )
    print("XIVBLEND_RENDER_STUDIO_TEST=PASS")


if __name__ == "__main__":
    main()
