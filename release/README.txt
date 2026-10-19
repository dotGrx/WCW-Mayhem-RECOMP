WCW Mayhem Recompiled - PC (unofficial community build)
=======================================================

An unofficial PC version of WCW Mayhem (PlayStation), made by statically
recompiling the PlayStation game to native code with psxrecomp. It does NOT
include the game. You need your own disc image of the PAL release.


WHAT YOU NEED
-------------
- Windows 10 or 11, 64-bit
- A graphics card with OpenGL 3.3 and an up-to-date driver
- Your own WCW Mayhem for PlayStation, PAL (Europe), SLES-02193, as a .cue
  file with its .bin next to it (the Redump dump "WCW Mayhem (Europe).cue")
- A controller is recommended (Xbox controllers work out of the box); the
  keyboard works too

No PlayStation BIOS is needed: the game runs on the bundled OpenBIOS (in
bios\, MIT licensed). Nothing else to install: the launcher carries its own
runtime.


FIRST RUN
---------
1. Extract the whole zip to a normal folder, for example C:\Games\WCW Mayhem.
   Do not run it from inside the zip, and avoid Program Files: the game keeps
   its settings and memory cards next to itself.
2. Double-click WCWMayhemLauncher.exe.
3. Under GAME FILES, choose your .cue. The launcher checks that the disc is
   the version this port is built from (track 1 MD5
   f05d7e5edad41f89e3c679dad83ea5f2). A different dump gets a warning but can
   still be played. Your disc image is never changed.
   (If you put your .cue and .bin in this folder, the launcher finds them by
   itself.)
4. Press PLAY.


"WINDOWS PROTECTED YOUR PC"
---------------------------
This build is not code-signed, so Windows SmartScreen may show "Windows
protected your PC" the first time. Choose "More info", then "Run anyway".


SETTINGS
--------
The launcher's SETTINGS page holds every option. They apply the next time the
game starts.

Display
- Internal resolution: Native up to 8K (1440p by default)
- Renderer, start in fullscreen, vertical sync, window size, texture
  filtering, antialiasing, CRT filter and scanlines

Enhancements
- Widescreen: 16:9 (default, on), 21:9 or fit to window (both untested).
  A genuinely wider view of the arena, not a stretch. Menus and videos stay
  4:3.
- PGXP precision (on by default): stops 3D models wobbling between pixels
  and big textures warping.
- Smooth motion (off by default): blends the game's frames up to your
  display's refresh rate. WCW Mayhem runs at 25 fps (PAL), and this does not
  add new frames, so fast moves show some ghosting.
- CD speed: emulated drive speed. WCW Mayhem paces its own loading screens,
  so this barely shortens them; turn it off if a load ever stalls.
- Fast boot: skip the PlayStation startup screen.
- Unlock all (off by default): every hidden wrestler, doppelganger select and
  the classic Nitro setting, the same as the Pay-Per-View passwords
  PLYHDNGYS, DPLGNGRS and PLYNTRCLSC, applied every session. Nothing is
  written to your memory card, so turning it off brings back the normal
  progression.

Controls
- Each player: Automatic (Xbox controllers are given to players in order;
  with none connected, player 1 uses the keyboard), Controller, Keyboard or
  None. Players 3 and 4 use the PlayStation multitap.
- Pad type: Digital (left stick moves) or DualShock (analog and vibration).
- Stick deadzone: Low, Medium or High.

Display and controller settings are saved in settings.toml in this folder,
the enhancements in mods\state.toml.


CONTROLS
--------
Xbox controller -> PlayStation:
  A = Cross, B = Circle, X = Square, Y = Triangle
  LB / RB = L1 / R1, LT / RT = L2 / R2
  View (Back) = Select, Menu (Start) = Start
  D-pad and left stick move

Keyboard (player 1):
  Arrow keys = move, X = Cross, S = Circle, Z = Square, A = Triangle
  Q / W = L1 / R1, E / R = L2 / R2
  Enter = Start, Right Shift = Select

Remap the controller in input.ini and the keyboard in keybinds.ini (both are
written next to the game the first time it runs; the launcher's Controls page
opens them). Alt+Enter switches between window and fullscreen.


YOUR SAVES
----------
Memory cards are in the saves folder next to WCWMayhem_Recompiled.exe. To
move to a newer version, copy that folder into the new one.


IF SOMETHING GOES WRONG
-----------------------
- "LOGS" in the launcher opens the logs folder: last-run.log holds the game's
  output and last-run-report.json how it exited. Attach them to a bug report
  on the project's GitHub page. Logs can contain folder paths that show your
  Windows user name; check before posting.
- If the launcher says the disc differs, it is a different dump or region.
  This port is built from the PAL (Europe) release, SLES-02193.


LEGAL
-----
This is an unofficial, non-commercial fan project. It is not affiliated with
or endorsed by WCW, Warner Bros. Discovery, Electronic Arts, Kodiak
Interactive, Sony or any other rights holder. No game data is included. Do not
share disc images. WCW and WCW Mayhem are trademarks of their owners.

This build includes psxrecomp, licensed under the PolyForm Noncommercial
License 1.0.0 (licenses\psxrecomp-LICENSE.txt): it is for non-commercial use
only. Other third-party notices are in THIRD-PARTY-NOTICES.txt and licenses\.

Source: https://github.com/dotGrx/WCW-Mayhem-RECOMP
