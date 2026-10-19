# psxrecomp patches

`scripts\setup-psxrecomp.ps1` applies these, in this order, to
[psxrecomp](https://github.com/mstan/psxrecomp) at commit
`8077e71098ce0347d7d41d267630bc856816cf77` (2026-10-06). Each touches different files. Like
psxrecomp itself, they are under the PolyForm Noncommercial License 1.0.0.

| Patch | What it does |
| --- | --- |
| `psxrecomp-pgxp-wrapped-shift.patch` | **Wobble-free wrestlers.** WCW Mayhem subdivides its wrestler polygons on the CPU: an edge midpoint is `lhu` + `sll 16` + `sra 17` of the sum of two projected coordinates (`func_80046478`). For a negative coordinate the zero-extended half shifted left by 16 wraps past 32 bits, and PGXP's CPU tier dropped the precise value on any wrapped shift, so those vertices fell back to whole pixels and wobbled against their precise neighbours. The patch keeps the precise value when the wrap is exactly this sign-extension idiom (a 16-bit source, shift of 16 or more): the guest result is exact modulo 2^32, so the same whole 2^32 steps are taken off the precise value. In a match this took precise vertices from 95.7% to 99.8% and triangles mixing precise and whole-pixel corners from 8.9% to 0.7%. (`runtime/src/pgxp.cpp`) |
| `psxrecomp-signed-x-bound-16bit.patch` | **Props at the 16:9 edges.** The game draws a prop (ring posts, turnbuckles, steps, barriers) only if one of its projected vertices has a screen X between -256 and +256 (`func_80047BB4`), which hid props in the extra widescreen width. The right edge is `addiu t3,zero,256`, which `[[widescreen.signed_x_bound]]` already widens; the left edge is `ori t2,zero,0xFF00`, -256 held as a 16-bit half for an unsigned compare, which the framework treated as +65280. The patch moves an `ORI` bound with bit 15 set as a signed 16-bit value and keeps the result 16 bits wide, in both the recompiler (`recompiler/src/code_generator.cpp`) and the interpreter (`runtime/src/dirty_ram_interp.c`), and documents it (`docs/config_schema.md`). |

To check them against a clean checkout:

```bat
git -C psxrecomp checkout --force 8077e71098ce0347d7d41d267630bc856816cf77
git -C psxrecomp apply --check ..\patches\psxrecomp-pgxp-wrapped-shift.patch
git -C psxrecomp apply --check ..\patches\psxrecomp-signed-x-bound-16bit.patch
```
