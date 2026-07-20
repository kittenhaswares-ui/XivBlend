"""Render an existing XivBlend file with the current studio setup without saving it.

This is a development/QA helper. Open the source .blend on Blender's command
line, pass this script, and put its arguments after ``--``.
"""

from __future__ import annotations

import argparse
import importlib.util
from pathlib import Path
import sys

import bpy


def arguments() -> argparse.Namespace:
    separator = sys.argv.index("--") if "--" in sys.argv else len(sys.argv)
    parser = argparse.ArgumentParser()
    parser.add_argument("--builder", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--hide", action="append", default=[])
    parser.add_argument("--resolution-percent", type=int, default=50)
    return parser.parse_args(sys.argv[separator + 1 :])


def load_builder(path: Path):
    spec = importlib.util.spec_from_file_location("xivblend_preview_builder", path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load builder: {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> None:
    options = arguments()
    builder = load_builder(Path(options.builder).resolve())
    objects = list(bpy.data.objects)

    hidden = {name.casefold() for name in options.hide}
    for obj in objects:
        if obj.name.casefold() in hidden:
            obj.hide_render = True
            obj.hide_viewport = True
        if obj.type == "MESH" and obj.material_slots and all(
            slot.material is None
            or slot.material.name.casefold() in {"null", "error"}
            for slot in obj.material_slots
        ):
            obj.hide_render = True
            obj.hide_viewport = True

    setup = bpy.data.collections.get(builder.SETUP_COLLECTION)
    if setup is None:
        setup = bpy.data.collections.new(builder.SETUP_COLLECTION)
        bpy.context.scene.collection.children.link(setup)
    builder.configure_scene_setup(bpy.context.scene, objects, setup)

    scene = bpy.context.scene
    scene.render.resolution_percentage = max(1, min(options.resolution_percent, 100))
    scene.render.filepath = str(Path(options.output).resolve())
    result = bpy.ops.render.render(write_still=True)
    if "FINISHED" not in result:
        raise RuntimeError("Blender did not finish the preview render")
    print(f"[xivblend-preview] {scene.render.filepath}")


if __name__ == "__main__":
    main()
