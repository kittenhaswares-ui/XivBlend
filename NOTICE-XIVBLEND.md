# XivBlend prototype notice

This prototype is derived from these pinned upstream revisions:

- **Meddle** v0.1.55.0, commit `312ad2610b74083376838964f5aebe6b5886449b`, Copyright its contributors, GNU Affero General Public License v3.0. Source: `https://github.com/PassiveModding/Meddle/tree/312ad2610b74083376838964f5aebe6b5886449b`.
- **MeddleTools** v0.1.10, commit `fc241c595996321cbb4c33a87d9e299ab9d3a0cd`, Copyright its contributors, GNU Affero General Public License v3.0. Source: `https://github.com/PassiveModding/MeddleTools/tree/fc241c595996321cbb4c33a87d9e299ab9d3a0cd`.
- **Dalamud VFXEditor**, commit `cd878d0e029d515acef723494ea4ffe5dbe19ade`, Copyright 2021 Michael Kaminsky, MIT License. XivBlend's in-process Havok PAP/SKLB loading and animation-sampling implementation is adapted from this work. Source: `https://github.com/0ceal0t/Dalamud-VFXEditor/tree/cd878d0e029d515acef723494ea4ffe5dbe19ade`.

The original Meddle license is retained as `LICENSE.txt`. The MeddleTools license is embedded beside its Python assets as `XivBlendBuilder/MEDDLETOOLS-LICENSE.txt`.

The VFXEditor MIT notice is reproduced below:

```text
Copyright 2021 Michael Kaminsky

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

XivBlend's prototype-specific orchestration, self-only UI, IPC manifest capture, Blender worker integration, and combined modifications are distributed under the repository's AGPL-3.0 terms, with the VFXEditor MIT notice retained above.

The complete corresponding modified source for this prototype is published at `https://github.com/kittenhaswares-ui/XivBlend`. Release binaries are built from that repository and must remain accompanied by this source and the applicable AGPL notices.

This is experimental software with limited live-client testing; broader character, equipment, and mod coverage is still required.
