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


def main() -> None:
    if not bpy.app.background or getattr(bpy.app, "factory_startup", False) is False:
        raise RuntimeError("Run this test with --background --factory-startup")

    # Simulate a file saved by the retired clay-preview implementation. The
    # current add-on must remove only its own tagged override on registration.
    legacy = bpy.data.materials.new("XivBlend Smooth Animation Preview")
    legacy["xivblend_runtime_preview_material"] = True
    legacy_name = legacy.name
    bpy.context.scene.view_layers[0].material_override = legacy

    sys.dont_write_bytecode = True
    addon = load_addon()
    addon.register()
    try:
        scene = bpy.context.scene
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
