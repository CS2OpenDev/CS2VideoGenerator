//===== Copyright 1996-2005, Valve Corporation, All rights reserved. ======//
//
// Purpose: Interfaces between the client.dll and engine
//
//===========================================================================//

// Credits @dtugend https://github.com/advancedfx/advancedfx-prop/blob/prop/cs2/sdk_src/public/cdll_int.h
//
// These vtable indices are reverse-engineered by the AdvancedFX project and must
// be re-synced whenever CS2 shifts the engine/client interfaces. Last synced
// 2026-07-10 for CS2 build 2000873 (patch 1.41.6.9; engine2.dll/client.dll dated
// 2026-07-09). The 2026-07-09 update shifted these vtables relative to the prior
// (2026-02) mapping:
//   ISource2EngineToClient: IsPlayingDemo 41->42, ExecuteClientCmd 49->51,
//                           GetLevelName 62->64, GetDemoFile 67->69
//   IDemoFile:              GetDemoTick 4->3
// Calling the stale slots faulted (IsPlayingDemo) or no-op'd (ExecuteClientCmd),
// which broke demo playback entirely.
//
// Only the methods actually called by the plugin are declared pure virtual
// (IsPlayingDemo / ExecuteClientCmd / GetDemoFile on the engine; GetDemoTick on
// the demo file). Every other slot is a placeholder that exists solely to position
// the used methods at their correct vtable index; placeholders have empty inline
// bodies (not pure) so the mock engine only needs to override the methods it
// actually implements.
//
// Platform note: ISource2EngineToClient is a pure interface (no virtual destructor),
// so its slots are identical on Windows and Linux. IDemoFile is the concrete
// CDemoPlayer with a virtual destructor, which under the Itanium ABI (Linux) takes an
// extra vtable slot, shifting everything from the destructor down by +1 on Linux — so
// IDemoFile carries a `#if !defined _WIN32` split. See docs/vtable-offsets-guide.md for
// the full investigation method and the rationale behind each split.
#include "interface.h"

abstract_class IDemoFile
{
  public:
    // IDemoFile is the engine's concrete CDemoPlayer, which has a virtual destructor.
    // On the Itanium ABI (Linux) a virtual destructor occupies two adjacent vtable slots
    // (complete + deleting) vs one on MSVC (Windows), so every slot from the destructor
    // down is shifted +1 on Linux. See docs/vtable-offsets-guide.md.
    virtual void _Unknown_000(void) {} // ~CDemoPlayer complete dtor (slot 0 on both)
#if !defined _WIN32
    virtual void _Unknown_LinuxDeletingDtor(void) {} // Itanium deleting dtor — Linux-only extra slot
#endif
    virtual void _Unknown_001(void) {}
    virtual int GetDemoStartTick(void) { return 0; } //: win 002 / linux 003
    virtual int GetDemoTick(void) = 0;               //: win 003 / linux 004  (used)
    virtual void _Unknown_004(void) {}
    virtual void _Unknown_005(void) {}
    virtual void _Unknown_006(void) {}
    virtual void _Unknown_007(void) {}
    virtual void _Unknown_008(void) {}
    virtual void _Unknown_009(void) {}
    virtual void _Unknown_010(void) {}
    virtual bool IsPlayingDemo(void) { return false; } //: win 011 / linux 012
    virtual bool IsDemoPaused(void) { return false; }  //: win 012 / linux 013
};

abstract_class ISource2EngineToClient
{
  public:
    virtual void _Unknown_000(void) {}
    virtual void _Unknown_001(void) {}
    virtual void _Unknown_002(void) {}
    virtual void _Unknown_003(void) {}
    virtual void _Unknown_004(void) {}
    virtual void _Unknown_005(void) {}
    virtual void _Unknown_006(void) {}
    virtual void _Unknown_007(void) {}
    virtual void _Unknown_008(void) {}
    virtual void _Unknown_009(void) {}
    virtual void _Unknown_010(void) {}
    virtual void _Unknown_011(void) {}
    virtual void _Unknown_012(void) {}
    virtual void _Unknown_013(void) {}
    virtual void _Unknown_014(void) {}
    virtual void _Unknown_015(void) {}
    virtual void _Unknown_016(void) {}
    virtual void _Unknown_017(void) {}
    virtual void _Unknown_018(void) {}
    virtual void _Unknown_019(void) {}
    virtual void _Unknown_020(void) {}
    virtual void _Unknown_021(void) {}
    virtual void _Unknown_022(void) {}
    virtual void _Unknown_023(void) {}
    virtual void _Unknown_024(void) {}
    virtual void _Unknown_025(void) {}
    virtual void _Unknown_026(void) {}
    virtual void _Unknown_027(void) {}
    virtual void _Unknown_028(void) {}
    virtual void _Unknown_029(void) {}
    virtual void _Unknown_030(void) {}
    virtual void _Unknown_031(void) {}
    virtual void _Unknown_032(void) {}
    virtual void _Unknown_033(void) {}
    virtual void _Unknown_034(void) {}
    virtual void _Unknown_035(void) {}
    virtual void _Unknown_036(void) {}
    virtual void _Unknown_037(void) {}
    virtual void _Unknown_038(void) {}
    virtual void _Unknown_039(void) {}
    virtual void _Unknown_040(void) {}
    virtual void _Unknown_041(void) {}
    virtual bool IsPlayingDemo(void) = 0;                         //: 042  (used)
    virtual const char *GetDemoFilePath(void) { return nullptr; } //: 043
    virtual void _Unknown_044(void) {}
    virtual void _Unknown_045(void) {}
    virtual void _Unknown_046(void) {}
    virtual void _Unknown_047(void) {}
    virtual void _Unknown_048(void) {}
    virtual void _Unknown_049(void) {}
    virtual void _Unknown_050(void) {}
    virtual void ExecuteClientCmd(int iUnk0MaybeSplitScreenSlotSetTo0, const char *pszCommands,
                                  bool bUnk2SetToTrue) = 0; //: 051  (used)
    virtual void _Unknown_052(void) {}
    virtual void _Unknown_053(void) {}
    virtual void _Unknown_054(void) {}
    virtual void _Unknown_055(void) {}
    virtual void _Unknown_056(void) {}
    virtual void _Unknown_057(void) {}
    virtual void _Unknown_058(void) {}
    virtual void _Unknown_059(void) {}
    virtual void _Unknown_060(void) {}
    virtual void _Unknown_061(void) {}
    virtual void _Unknown_062(void) {}
    virtual void _Unknown_063(void) {}
    virtual char const *GetLevelName(void) { return nullptr; }      //: 064
    virtual char const *GetLevelNameShort(void) { return nullptr; } //: 065
    virtual void _Unknown_066(void) {}
    virtual void _Unknown_067(void) {}
    virtual void _Unknown_068(void) {}
    virtual IDemoFile *GetDemoFile(void) = 0; //: 069  (used)
};

// Vtable slots patched for the engine-thread frame hook (the CS Demo Manager
// cs2-server-plugin pattern: hook Source2GameClients001::ClientFullyConnect, and
// from it patch Source2Client002::FrameStageNotify so console commands can be
// executed on the engine thread - ExecuteClientCmd is not thread-safe from
// background threads). Unlike IDemoFile above, both interfaces are pure (no
// virtual destructor), so these slots are identical on Windows and Linux - CSDM
// ships the same indices unconditionally on both platforms.
constexpr int kSource2GameClients_ClientFullyConnectSlot = 15; // Source2GameClients001
constexpr int kSource2Client_FrameStageNotifySlot = 36;        // Source2Client002

enum ClientFrameStage_t
{
    FRAME_UNDEFINED = -1, // (haven't run any frames yet)
    FRAME_START,

    // A network packet is being recieved
    FRAME_NET_UPDATE_START,
    // Data has been received and we're going to start calling PostDataUpdate
    FRAME_NET_UPDATE_POSTDATAUPDATE_START,
    // Data has been received and we've called PostDataUpdate on all data recipients
    FRAME_NET_UPDATE_POSTDATAUPDATE_END,
    // We've received all packets, we can now do interpolation, prediction, etc..
    FRAME_NET_UPDATE_END,

    // We're about to start rendering the scene
    FRAME_RENDER_START,
    // We've finished rendering the scene.
    FRAME_RENDER_END
};
