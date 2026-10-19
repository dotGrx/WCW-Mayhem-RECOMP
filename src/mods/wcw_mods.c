/* WCW Mayhem (SLES-02193) trusted mod plugins.
 *
 * Selected by id from mods/preloaded/packages/wcw.enhancement.widescreen.
 * Archives never supply native code; these implementations are linked into
 * the game executable and activated only when the package feature is on. */
#include "mod_plugins.h"

#include <stdlib.h>
#include <string.h>

/* Widescreen: native-wide view at the selected aspect. The cull sites that
 * keep the revealed side bands populated live in game.toml [widescreen.cull]. */
static void wcw_widescreen_activate(void) {
    char aspect[16] = {0};
    if (!psx_mod_current_option_value("aspect", aspect, sizeof(aspect)))
        strcpy(aspect, "16:9");
    if (strcmp(aspect, "21:9") == 0)
        (void)psx_mod_set_fixed_display_aspect(21u, 9u);
    else if (strcmp(aspect, "fit") == 0) {
        (void)psx_mod_set_fixed_display_aspect(16u, 9u);
        (void)psx_mod_set_adaptive_display_aspect(0u, 0u);
    } else
        (void)psx_mod_set_fixed_display_aspect(16u, 9u);
}

/* Smooth motion: presentation-only frame blending. WCW Mayhem's main loop
 * (func_80016554) waits until two VBlanks have passed since the last frame
 * (0x800A9534 - 0x800A953C < 2), and its logic is fixed-step per frame, so the
 * game itself stays at 25 fps (PAL). The FLIP source spans each crossfade over
 * the game's whole two-VBlank frame instead of blending one VBlank and holding
 * the next. */
static void wcw_smooth_motion_activate(void) {
    char rate[16] = {0};
    char blend[24] = {0};
    if (!psx_mod_current_option_value("rate", rate, sizeof(rate)))
        strcpy(rate, "display");
    if (!psx_mod_current_option_value("blend", blend, sizeof(blend)))
        strcpy(blend, "motion-adaptive");
    (void)psx_mod_set_frame_interpolation_source(PSX_MOD_FRAME_SOURCE_FLIP);
    (void)psx_mod_set_frame_interpolation_blend(
        strcmp(blend, "linear") == 0 ? PSX_MOD_FRAME_INTERPOLATION_LINEAR
                                     : PSX_MOD_FRAME_INTERPOLATION_MOTION_ADAPTIVE);
    (void)psx_mod_set_frame_interpolation(
        strcmp(rate, "display") == 0 ? 0u : (uint32_t)atoi(rate));
}

/* Unlock all: the game's own Pay-Per-View cheat flags. The PPV password
 * handler (overlay func_800C2D00, jump table 0x800B3E28) ORs one bit per
 * password into the session word at 0x8009CFF4 (0x8009C4A0 + 0xB54):
 *   0x0100 PLYHDNGYS  hidden wrestlers unlocked
 *   0x0400 DPLGNGRS   doppelganger select
 *   0x0800 PLYNTRCLSC classic Nitro setting
 * The game rebuilds this word (and a password only lasts one session), so the
 * bits are re-applied every VBlank once the game is running. Debug prints,
 * the quest cheat, bionic CAWs and the developer test case are left alone. */
#define WCW_PPV_FLAGS_ADDR 0x8009CFF4u
#define WCW_UNLOCK_BITS    (0x0100u | 0x0400u | 0x0800u)

static void wcw_unlock_all_vblank(void) {
    if (!psx_mod_game_started()) return;
    const uint32_t flags = psx_mod_read_word(WCW_PPV_FLAGS_ADDR);
    if ((flags & WCW_UNLOCK_BITS) != WCW_UNLOCK_BITS)
        psx_mod_write_word(WCW_PPV_FLAGS_ADDR, flags | WCW_UNLOCK_BITS);
}

PSX_MOD_CONSTRUCTOR(register_wcw_mods) {
    (void)psx_mod_register_activation_plugin("wcw.widescreen",
                                             wcw_widescreen_activate);
    (void)psx_mod_register_activation_plugin("wcw.smooth-motion",
                                             wcw_smooth_motion_activate);
    (void)psx_mod_register_vblank_plugin("wcw.unlock-all",
                                         wcw_unlock_all_vblank);
}
