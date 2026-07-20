"""Stable Blender CLI entry point for the Clean Extract plugin worker."""

from pathlib import Path
import runpy


runpy.run_path(str(Path(__file__).with_name("build_character.py")), run_name="__main__")

