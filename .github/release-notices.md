# XivBlend Prototype 0.0.18

This update adds the genuinely shadowed portrait look requested after the earlier Detail preset proved too close to Beauty.

## What changed

- Adds **Render Studio → Mood**. It moves the existing three-light rig into a photographer-style setup: a small warm side key, almost no frontal/ambient fill, and a narrow cool rear kicker. The result has strong Rembrandt-style facial and body shadow without adding render-heavy volumes or extra lights.
- Keeps **Beauty** and **Detail** as separate choices. Switching between any mode restores Beauty's exact energy, size, position, rotation, color, and shadow baselines first, so repeated changes and files saved in Mood cannot drift.
- Makes final Cycles faces less waxy by temporarily reducing only the unlinked subsurface weight on materials explicitly identified as FFXIV face skin. Normal, pore-detail, roughness, specular, color, makeup, and mod texture links stay untouched.
- Restores the original face value for Eevee Preview, Solid Animate, saving, and add-on removal. Linked/custom skin inputs, body skin, orphan materials, and unrelated user lights are skipped.
- Keeps the dense alpha-card hair/fur correction from 0.0.17.
- Adds Blender 5.0 and 5.2 tests for Mood geometry and color, exact preset round-trips, saved-Mood reloads, face-only scope, linked-input safety, and fresh-export baselines.

## Install and test

1. Update or reload XivBlend 0.0.18.
2. In **Animations**, press **Set Up / Update Animation Browser**.
3. Restart Blender so it loads browser 0.9.0.
4. Reopen an existing XivBlend character file; a character re-export is not required.
5. Open the Blender `N` sidebar → **XivBlend → Render Studio** and choose **Mood**.

Mood is a final Cycles look, so use **Animate** or **Preview** while posing and switch to Mood when judging or rendering the picture.
