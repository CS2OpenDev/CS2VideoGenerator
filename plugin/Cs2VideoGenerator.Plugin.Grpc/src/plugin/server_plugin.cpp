/**
 * @file server_plugin.cpp
 * @brief CS2 Video Generator server plugin entry point and vtable hooking.
 *
 * This file implements the plugin that loads alongside Counter-Strike 2's
 * server library. It intercepts the Source2ServerConfig001 interface via
 * vtable hooking to inject a gRPC client that enables remote control of
 * demo playback (loading, seeking, pausing, recording) for automated
 * video generation.
 *
 * The plugin is loaded by CS2 via the CreateInterface export. On first
 * call, it loads the real server library, hooks the Connect/Shutdown
 * vtable entries, starts a gRPC client to communicate with the video
 * generation orchestrator, and restores any modified gameinfo.gi files.
 */

// IMPORTANT: gRPC/protobuf headers MUST come before HL2SDK headers.
// HL2SDK's platform.h defines macros (EXPLICIT, etc.) that break protobuf.
#include "Cs2PluginClient.h"
#include <grpcpp/grpcpp.h>

#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <memory>
#include <thread>

// HL2SDK headers - must come after gRPC/protobuf
#include "cdll_interfaces.h"
#include "icvar.h"
#include "tier1/utlstring.h" // CUtlString: the out-parameter type of tier0's SaveKV3AsJSON
#ifdef _WIN32
#define SERVER_LIB_PATH "\\csgo\\bin\\win64\\server.dll"
#else
#include <dlfcn.h>
#include <sys/mman.h>
#define SERVER_LIB_PATH "/csgo/bin/linuxsteamrt64/libserver.so"
#define PAGESIZE 4096
#endif

// IDKW registering a cmd on Linux makes the game process exit with a non zero
// code (Segmentation fault)
#ifdef _WIN32
#define CON_COMMAND_ENABLED 1
#endif

using std::string;

/**
 * @brief Retrieves a symbol address from a loaded shared library.
 * @param lib Handle to the loaded library (HMODULE on Windows, dlopen handle on
 * Linux).
 * @param name Name of the symbol to look up.
 * @return Pointer to the symbol, or nullptr if not found.
 */
void *GetLibAddress(void *lib, const char *name)
{
#if defined _WIN32
    return GetProcAddress((HMODULE)lib, name);
#else
    return dlsym(lib, name);
#endif
}

/**
 * @brief Returns a platform-specific string describing the last library loading
 * error.
 * @return Static buffer with error description (Windows) or dlerror() result
 * (Linux).
 */
char *GetLastErrorString()
{
#ifdef _WIN32
    DWORD error = GetLastError();
    static char s[_MAX_U64TOSTR_BASE2_COUNT];
    sprintf(s, "%lu", error);

    return s;
#else
    return dlerror();
#endif
}

/**
 * @brief Loads a shared library from disk.
 * @param path Filesystem path to the library (.dll on Windows, .so on Linux).
 * @return Handle to the loaded library, or nullptr on failure.
 */
void *LoadLib(const char *path)
{
#ifdef _WIN32
    return LoadLibrary((const tchar *)path);
#else
    return dlopen(path, RTLD_NOW);
#endif
}

typedef bool (*AppSystemConnectFn)(IAppSystem *appSystem, CreateInterfaceFn factory);
typedef void (*AppSystemShutdownFn)();
/** @brief Source2Client002::FrameStageNotify, slot 36. */
typedef void (*FrameStageNotifyFn)(void *thisptr, ClientFrameStage_t stage);
/** @brief Source2GameClients001::ClientFullyConnect, slot 15. */
typedef void (*ClientFullyConnectFn)(void *thisptr, int playerSlot);

/** @brief The engine's original CreateInterface factory function. */
CreateInterfaceFn factory = nullptr;
/** @brief Original Source2ServerConfig001 Connect function (saved before
 * hooking). */
AppSystemConnectFn serverConfigConnect = nullptr;
/** @brief Original Source2ServerConfig001 Shutdown function (saved before
 * hooking). */
AppSystemShutdownFn serverConfigShutdown = nullptr;
/** @brief CreateInterface from the real server library (server.dll /
 * libserver.so). */
CreateInterfaceFn serverCreateInterface = nullptr;
/** @brief Cached pointer to the Source2EngineToClient interface. */
ISource2EngineToClient *engineToClient = nullptr;
ICvar *g_pCVar = nullptr;
std::thread grpcThread;
string gameInfoPath;
/** @brief Path to the gameinfo.gi backup file created during plugin setup. */
string gameInfoBackupPath;
std::atomic_bool isQuitting{false};
std::shared_ptr<grpc::Channel> grpcChannel;
std::unique_ptr<Cs2PluginClient> grpcClient;
/** @brief Original Source2GameClients001::ClientFullyConnect (saved before hooking). */
ClientFullyConnectFn originalClientFullyConnect = nullptr;
/** @brief Original Source2Client002::FrameStageNotify (saved before hooking). */
FrameStageNotifyFn originalFrameStageNotify = nullptr;
/** @brief Whether the FrameStageNotify hook has been installed (one-shot). */
std::atomic_bool frameStageNotifyHooked{false};
/** @brief One-shot guard for the "Source2Client002 first resolved" log. */
std::atomic_bool source2ClientResolveLogged{false};
/** @brief One-shot guard for the "Source2Client002 not yet available at Connect" log. */
std::atomic_bool connectSiteMissLogged{false};
/** @brief One-shot guard for the "first engine frame observed" log. */
std::atomic_bool firstFrameStageNotifyLogged{false};
/**
 * @brief Wall-clock origin for the "ms since plugin load" figures in the hook logs.
 *
 * Set once in CreateInterface's first-call block - the earliest code this plugin
 * runs. The interesting quantity is how long client.dll takes to come up relative
 * to it, which is what decides whether the polling-loop retry closes the window
 * before the host issues its first command batch.
 */
std::chrono::steady_clock::time_point pluginLoadTime = std::chrono::steady_clock::now();

/**
 * @brief Milliseconds elapsed since the plugin's CreateInterface was first called.
 * @return Elapsed milliseconds, suitable for a `%lld` format specifier.
 */
long long MillisecondsSincePluginLoad()
{
    return (long long)std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() -
                                                                            pluginLoadTime)
        .count();
}

/**
 * @brief Appends a message to the csvg.log file.
 * @param pMsg Null-terminated string to write.
 */
void LogToFile(const char *pMsg)
{
    FILE *pFile = fopen("csvg.log", "a");
    if (pFile == NULL)
    {
        return;
    }

    fprintf(pFile, "%s\n", pMsg);
    fclose(pFile);
}

/**
 * @brief Deletes the csvg.log file. Called at plugin startup for a clean log.
 */
void DeleteLogFile()
{
    remove("csvg.log");
}

/**
 * @brief Logs a formatted message to the CS2 console (with color) and to
 * csvg.log.
 * @param msg printf-style format string.
 * @param ... Variable arguments matching the format string.
 */
void Log(const char *msg, ...)
{
    va_list args;
    va_start(args, msg);
    char buf[1024] = {};
    vsnprintf(buf, sizeof(buf), msg, args);
    ConColorMsg(Color(227, 0, 255, 255), (const tchar *)"CSVG: %s\n", buf);
    va_end(args);
    LogToFile(buf);
}

/**
 * @brief Displays a fatal error and terminates the CS2 process.
 *
 * On Windows, shows a message box then exits (Plat_FatalErrorFunc crashes
 * since the Armory update). On Linux, calls Plat_FatalErrorFunc directly.
 *
 * @param msg printf-style format string.
 * @param ... Variable arguments matching the format string.
 */
void PluginError(const char *msg, ...)
{
    va_list args;
    va_start(args, msg);
    char buf[1024] = {};
    vsnprintf(buf, sizeof(buf), msg, args);
    va_end(args);

    // Since the "Armory" update, calling Plat_FatalErrorFunc crashes the game on
    // Windows.
#ifdef _WIN32
    Plat_MessageBox("Error", (const tchar *)buf);
    Plat_ExitProcess(1);
#else
    Plat_FatalErrorFunc("%s", buf);
#endif
}

/**
 * @brief Overwrites one vtable slot with a hook, unprotecting only the page that
 *        actually holds the slot.
 *
 * Shared by every vtable patch in this plugin (Source2ServerConfig001 Connect /
 * Shutdown, Source2GameClients001 ClientFullyConnect, Source2Client002
 * FrameStageNotify). A patch failure is fatal: silently running with a half-hooked
 * interface would fail in a far more confusing way than an explicit abort.
 *
 * @param vtable The interface's vtable pointer array.
 * @param index Slot index to overwrite.
 * @param newFunc Replacement function pointer.
 */
void PatchVTableEntry(void **vtable, int index, void *newFunc)
{
#ifdef _WIN32
    size_t protectSize = sizeof(void *) * (index + 1);
    DWORD oldProtect = 0;
    if (!VirtualProtect(vtable, protectSize, PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        PluginError("VirtualProtect PAGE_EXECUTE_READWRITE failed: %d", GetLastError());
    }
    vtable[index] = newFunc;
    DWORD ignore = 0;
    if (!VirtualProtect(vtable, protectSize, oldProtect, &ignore))
    {
        PluginError("VirtualProtect restore failed: %d", GetLastError());
    }
#else
    // The page containing the slot, not the page containing the vtable base: a
    // high-index slot (FrameStageNotify is 36) can sit on the following page.
    void *slotAddr = (void *)&vtable[index];
    void *pageStart = (void *)((uintptr_t)slotAddr & ~(uintptr_t)(PAGESIZE - 1));
    if (mprotect(pageStart, PAGESIZE, PROT_READ | PROT_WRITE | PROT_EXEC) != 0)
    {
        PluginError("mprotect failed: %s", strerror(errno));
    }
    vtable[index] = newFunc;
    if (mprotect(pageStart, PAGESIZE, PROT_READ | PROT_EXEC) != 0)
    {
        PluginError("mprotect restore failed: %s", strerror(errno));
    }
#endif
}

/**
 * @brief Removes FCVAR_HIDDEN and FCVAR_DEVELOPMENTONLY flags from all console
 *        commands and convars.
 *
 * Since the "Armory" update CS2 ships `startmovie` (and `spec_lock_to_accountid`)
 * hidden, and a hidden command is not dispatchable from ExecuteClientCmd - the
 * InEngine capture provider's `startmovie` would silently do nothing. CS Demo
 * Manager's plugin strips the same two flags for the same reason.
 *
 * Iterates by raw access index rather than ICvar's FindFirst / FindNext walk, because
 * that walk is exactly the enumeration hidden entries are meant to be absent from. The
 * terminator is the sentinel ConCommandData that GetConCommandData returns for an
 * invalid ref; cvars terminate on an invalid ref instead, since ConVarRefAbstract
 * publishes IsValidRef.
 *
 * Called once from Connect, after ConVar_Register. Real plugin only - the mock has no
 * cvar system.
 */
static void UnhideCommandsAndCvars()
{
    if (g_pCVar == nullptr)
    {
        Log("Cannot unhide commands and cvars: VEngineCvar007 not available - `startmovie` will stay hidden");
        return;
    }

    uint64 flagsToRemove = (FCVAR_HIDDEN | FCVAR_DEVELOPMENTONLY);
    int unhiddenCommands = 0;
    int unhiddenCvars = 0;

    ConCommandData *data = g_pCVar->GetConCommandData(ConCommandRef());
    for (ConCommandRef concmd = ConCommandRef((uint16)0); concmd.GetRawData() != data;
         concmd = ConCommandRef(concmd.GetAccessIndex() + 1))
    {
        if (concmd.GetFlags() & flagsToRemove)
        {
            concmd.RemoveFlags(flagsToRemove);
            ++unhiddenCommands;
        }
    }

    for (ConVarRefAbstract ref(ConVarRef((uint16)0)); ref.IsValidRef();
         ref = ConVarRefAbstract(ConVarRef(ref.GetAccessIndex() + 1)))
    {
        if (ref.GetFlags() & flagsToRemove)
        {
            ref.RemoveFlags(flagsToRemove);
            ++unhiddenCvars;
        }
    }

    Log("Unhid %d console command(s) and %d cvar(s) (cleared FCVAR_HIDDEN|FCVAR_DEVELOPMENTONLY) - required for "
        "`startmovie` since the Armory update",
        unhiddenCommands, unhiddenCvars);
}

/**
 * @brief Determines the running CS2 build number for the version handshake.
 *
 * Reads `<gamedir>/csgo/steam.inf` (the same directory that holds gameinfo.gi)
 * and returns the `ClientVersion=` value, which is the CS2 build number Steam
 * updates on every game update. This file-based source is used instead of an
 * engine query because none of the interfaces this plugin consumes expose a
 * build number, while steam.inf is guaranteed to exist in every install and
 * is always in sync with the running binary (Steam replaces them together).
 *
 * @return The CS2 build number as a string (e.g. "14093"), or an empty string
 *         if steam.inf cannot be read. Best-effort: an unknown version makes the
 *         orchestrator warn, never fail.
 */
std::string GetCsvgGameVersion()
{
    static std::string cachedVersion;
    static bool resolved = false;
    if (resolved)
    {
        return cachedVersion;
    }
    resolved = true;

    const char *gameDirectory = Plat_GetGameDirectory();
    if (gameDirectory == nullptr)
    {
        return cachedVersion;
    }

    string steamInfPath = string(gameDirectory) + "/csgo/steam.inf";
    std::ifstream steamInf(steamInfPath);
    if (!steamInf.good())
    {
        Log("steam.inf not found at %s - game version unknown", steamInfPath.c_str());
        return cachedVersion;
    }

    const string key = "ClientVersion=";
    string line;
    while (std::getline(steamInf, line))
    {
        // Trim trailing CR (steam.inf ships with Windows line endings)
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n' || line.back() == ' '))
        {
            line.pop_back();
        }

        if (line.compare(0, key.size(), key) == 0)
        {
            cachedVersion = line.substr(key.size());
            break;
        }
    }

    if (cachedVersion.empty())
    {
        Log("ClientVersion not found in %s - game version unknown", steamInfPath.c_str());
    }

    return cachedVersion;
}

/**
 * @brief Lazily retrieves the ISource2EngineToClient interface.
 *
 * On first call after the factory is available, queries the engine for the
 * Source2EngineToClient001 interface and caches the result.
 *
 * @return Pointer to the engine interface, or nullptr if the factory is not yet
 * set.
 */
ISource2EngineToClient *GetEngine()
{
    if (engineToClient != NULL)
    {
        return engineToClient;
    }

    if (factory == NULL)
    {
        return NULL;
    }

    engineToClient = (ISource2EngineToClient *)factory("Source2EngineToClient001", NULL);

    return engineToClient;
}

/**
 * @brief Restores the original gameinfo.gi from its backup copy.
 *
 * The video generator modifies gameinfo.gi to inject the plugin. After the
 * plugin has loaded, this function restores the original file and deletes
 * the backup so CS2 boots normally on next launch.
 */
void RestoreGameinfoFile()
{
    std::ifstream filebackupFile(gameInfoBackupPath);
    if (!filebackupFile.good())
    {
        Log("gameinfo.gi backup file doesn't exist");
        filebackupFile.close();
        return;
    }

    std::ofstream destination(gameInfoPath);
    destination << filebackupFile.rdbuf();

    filebackupFile.close();
    destination.close();

    int result = remove(gameInfoBackupPath.c_str());
    if (result == 0)
    {
        Log("Backup file deleted successfully");
    }
    else
    {
        Log("Error deleting backup file");
    }
}

/**
 * @brief Initializes the gRPC client and starts the bidirectional stream on a
 *        background thread.
 *
 * Hides the demo UI, creates a gRPC channel to localhost:50051, and spawns a thread
 * running the game session stream. Console commands are unhidden earlier, by Connect
 * itself, so that step is not buried inside this function's try/catch.
 */
void StartGrpcClient()
{
    try
    {
        // Hide the demo playback UI (added in the 23/05/2024 CS2 update). Best-effort
        // this early: if the engine isn't up yet, the client handles it.
        auto engine = GetEngine();
        if (engine != nullptr)
        {
            engine->ExecuteClientCmd(0, "demo_ui_mode 0", true);
        }

        grpcChannel = grpc::CreateChannel("localhost:50051", grpc::InsecureChannelCredentials());
        // GetEngine is passed as a function, not a pointer: the engine may not be
        // available yet at startup.
        grpcClient = std::make_unique<Cs2PluginClient>(grpcChannel, &GetEngine);
        grpcThread = std::thread([]() { grpcClient->StartGameSession(); });
        Log("gRPC client started");
    }
    catch (...)
    {
        Log("Failed to start gRPC client");
    }
}

/**
 * @brief Hooked Source2Client002::FrameStageNotify (slot 36).
 *
 * The engine thread's per-frame callback. At FRAME_START it hands control to the
 * gRPC client (drain queued console commands, read + process engine state), then
 * always chains the original. Everything else passes straight through: this runs
 * once per rendered frame, so anything but the FRAME_START branch must be free.
 *
 * This hook exists because ISource2EngineToClient::ExecuteClientCmd is not
 * thread-safe off the engine thread (the same reason CS Demo Manager's plugin hooks
 * this slot); before it, every console command was issued from a background gRPC or
 * polling thread.
 */
// present_capture.cpp: the DXGI hook behind the InEngineHooked provider. Installed from
// the engine-frame hook below; inert unless the CSVG host is
// listening on the frame pipe when a startmovie executes.
void CsvgPresentCaptureEnsureHook();

void NewFrameStageNotify(void *thisptr, ClientFrameStage_t stage)
{
    // One-shot: answers whether FrameStageNotify fires at the main menu (before any
    // demo is loaded) or only once a demo/map is being brought up. If this line lands
    // before the first playdemo, the queued command batch is safe by construction; if
    // it only lands after, the queue holds until the engine starts rendering the demo.
    if (!firstFrameStageNotifyLogged.exchange(true))
    {
        Log("First engine frame observed via FrameStageNotify (stage=%d, %lld ms after plugin load) - the "
            "engine-thread command queue is now draining",
            (int)stage, MillisecondsSincePluginLoad());
    }

    // One relaxed load after the first frame; the D3D device only exists once the
    // engine is rendering, which is exactly what this callback proves.
    CsvgPresentCaptureEnsureHook();

    if (stage == ClientFrameStage_t::FRAME_START && !isQuitting.load() && grpcClient)
    {
        grpcClient->OnEngineFrame();
    }

    if (originalFrameStageNotify != nullptr)
    {
        originalFrameStageNotify(thisptr, stage);
    }
}

/**
 * @brief Installs the FrameStageNotify hook if Source2Client002 is resolvable.
 *
 * Idempotent and cheap when already installed (one atomic load), because the gRPC
 * client's polling loop calls this on every cycle until it succeeds.
 *
 * Attempted from three sites, because the client interface is not available at
 * plugin-load time (client.dll is not up yet):
 *   1. the hooked Source2ServerConfig001::Connect - free, and always too early;
 *   2. the polling loop - the site that actually installs it, a few seconds into
 *      boot, driven by interface availability rather than game state (HLAE's
 *      AfxHookSource2 installs the same slot at client.dll module-load time for the
 *      same reason);
 *   3. Source2GameClients001::ClientFullyConnect - a backstop only. It does not fire
 *      at the main menu, and waiting for it was the bug: the plugin has to execute
 *      `playdemo` to make it fire, and executing that batch unhooked - from gRPC
 *      background threads into a mid-load engine - crashed the engine first.
 *
 * The patch is a single aligned pointer store via PatchVTableEntry, so installing
 * from the polling thread is safe.
 *
 * @param site Human-readable install site, logged so the log answers "which one won".
 * @return true when the hook is installed (now or previously).
 */
bool TryInstallFrameStageNotifyHook(const char *site)
{
    if (frameStageNotifyHooked.load())
    {
        return true;
    }
    if (factory == nullptr)
    {
        return false;
    }

    void *client = factory("Source2Client002", nullptr);
    if (client == nullptr)
    {
        return false;
    }

    // One-shot, emitted before the patch so the log carries the resolve timing even if
    // the patch itself were to abort: this is the "how long does client.dll take?"
    // measurement the retry loop was sized against.
    if (!source2ClientResolveLogged.exchange(true))
    {
        Log("Source2Client002 first resolved at %s (%lld ms after plugin load)", site, MillisecondsSincePluginLoad());
    }

    auto vtable = *(void ***)client;
    originalFrameStageNotify = (FrameStageNotifyFn)vtable[kSource2Client_FrameStageNotifySlot];
    PatchVTableEntry(vtable, kSource2Client_FrameStageNotifySlot, (void *)&NewFrameStageNotify);
    frameStageNotifyHooked.store(true);
    // The install site is the one thing a log has to answer: it says how much of the
    // session ran with a real engine-thread queue behind it.
    Log("Hooked Source2Client002::FrameStageNotify (slot %d) at %s (%lld ms after plugin load) - console commands "
        "now execute on the engine thread",
        kSource2Client_FrameStageNotifySlot, site, MillisecondsSincePluginLoad());
    return true;
}

/**
 * @brief Hooked Source2GameClients001::ClientFullyConnect (slot 15).
 *
 * A backstop install site, kept for the case where the polling loop is not running.
 * ClientFullyConnect does not fire at the main menu - only during demo/map bring-up -
 * so it can never be the site that makes the first demo load safe.
 */
void NewClientFullyConnect(void *thisptr, int playerSlot)
{
    if (!TryInstallFrameStageNotifyHook("ClientFullyConnect"))
    {
        Log("Source2Client002 not available at ClientFullyConnect - FrameStageNotify hook NEVER installed; "
            "console commands will stay queued until it is");
    }

    if (originalClientFullyConnect != nullptr)
    {
        originalClientFullyConnect(thisptr, playerSlot);
    }
}

bool CsvgEngineFrameHookInstalled()
{
    return frameStageNotifyHooked.load();
}

/**
 * @brief Per-binary hook-install retry, called from the gRPC client's polling loop.
 *
 * The real plugin's implementation: client.dll is not loaded when Connect runs, so
 * the interface is polled for until it resolves - availability-driven, not
 * game-state-driven. The mock's implementation is a no-op (its frame pump stands in
 * for the hook). Cheap and idempotent once installed.
 *
 * @return true when the engine-thread frame hook is installed.
 */
bool CsvgTryInstallEngineFrameHook()
{
    return TryInstallFrameStageNotifyHook("EnginePolling");
}

/**
 * @brief Per-binary execution discipline: the real plugin NEVER direct-executes.
 *
 * ISource2EngineToClient::ExecuteClientCmd is not thread-safe off the engine thread.
 * Calling it from a gRPC background thread during CS2's demo-load window crashes the
 * engine deterministically (engine2.dll AV), so on this binary a command with no hook
 * yet waits in the queue rather than taking a "legacy" direct call. The retry above
 * makes that wait a few seconds at boot. The mock returns false: with its frame pump
 * disabled it must still execute, and nothing there is thread-unsafe.
 *
 * @return true - always, on the real plugin.
 */
bool CsvgCommandsRequireEngineThread()
{
    return true;
}

// Game-event telemetry hook ("game-event-telemetry", off by default).
//
// HLAE's technique (AfxHookSource2/GameEvents.cpp): locate CGameEventManager's
// vtable in client.dll by its RTTI type-descriptor name, then hook
// FireEventClientSide (slot 8) and serialize the event's KV3 payload to JSON with
// tier0's exported SaveKV3AsJSON.
//
// Windows-only for now, and deliberately so: the vtable lookup is MSVC-RTTI
// specific (RTTICompleteObjectLocator chains that have no Itanium equivalent), and
// the Itanium approach - typeinfo symbol lookup in the client .so - is a separate
// piece of work. The capability token is withheld on Linux, so the orchestrator
// throws NotSupportedException there instead of silently reporting nothing (the
// house rule for state-changing calls with no legacy path).
//
// The hook is installed lazily on first enable (hard requirement: never at load),
// is idempotent, and is never uninstalled - once installed it is a cheap
// pass-through whenever telemetry is disabled.
#ifdef _WIN32
namespace
{
typedef bool (*FireEventClientSideFn)(void *thisptr, void *event);
FireEventClientSideFn originalFireEventClientSide = nullptr;
void **gameEventManagerVtable = nullptr;

/// tier0's exported KV3 -> JSON serializer, resolved by decorated name at install time.
typedef bool (*SaveKV3AsJSONFn)(const void *kv, CUtlString *error, CUtlString *output);
SaveKV3AsJSONFn g_SaveKV3AsJSON = nullptr;

/// Slot indices on CGameEventManager's vtable, per HLAE's GameEvents.cpp.
constexpr int kFireEventClientSideSlot = 8;

/**
 * @brief The subset of IGameEvent this plugin reads.
 *
 * Only GetName (slot 1) and GetDataKeys (the final slot) are used; every other slot
 * is a placeholder that positions them correctly, exactly as cdll_interfaces.h does
 * for the engine interfaces. Layout mirrors advancedfx-prop's igameevents.h.
 */
class IGameEventReader
{
  public:
    virtual ~IGameEventReader() = default;
    virtual const char *GetName() const = 0; //: 001 (used)
    // clang-format off
    virtual void _Unknown_002() {}           // GetID
    virtual void _Unknown_003() {}           // IsReliable
    virtual void _Unknown_004() {}           // IsLocal
    virtual void _Unknown_005() {}           // IsEmpty
    virtual void _Unknown_006() {}           // GetBool
    virtual void _Unknown_007() {}           // GetInt
    virtual void _Unknown_008() {}           // GetUint64
    virtual void _Unknown_009() {}           // GetFloat
    virtual void _Unknown_010() {}           // GetString
    virtual void _Unknown_011() {}           // GetPtr
    virtual void _Unknown_012() {}           // GetEHandle
    virtual void _Unknown_013() {}           // GetEntity
    virtual void _Unknown_014() {}           // GetEntityIndex
    virtual void _Unknown_015() {}           // GetPlayerSlot
    virtual void _Unknown_016() {}           // GetPlayerController
    virtual void _Unknown_017() {}           // GetPlayerPawn
    virtual void _Unknown_018() {}           // GetPawnEHandle
    virtual void _Unknown_019() {}           // GetPawnEntityIndex
    virtual void _Unknown_020() {}           // SetBool
    virtual void _Unknown_021() {}           // SetInt
    virtual void _Unknown_022() {}           // SetUint64
    virtual void _Unknown_023() {}           // SetFloat
    virtual void _Unknown_024() {}           // SetString
    virtual void _Unknown_025() {}           // SetPtr
    virtual void _Unknown_026() {}           // SetEntity(CEntityInstance*)
    virtual void _Unknown_027() {}           // SetEntity(CEntityIndex)
    virtual void _Unknown_028() {}           // SetPlayer(CEntityInstance*)
    virtual void _Unknown_029() {}           // SetPlayer(CPlayerSlot)
    virtual void _Unknown_030() {}           // SetPlayerRaw
    virtual void _Unknown_031() {}           // HasKey
    virtual void _Unknown_032() {}           // CreateVMTable
    // clang-format on
    virtual const void *GetDataKeys() const = 0; //: last slot (used)
};

/**
 * @brief Serializes a fired game event and offers it to the plugin client.
 *
 * Runs on the engine thread inside the FireEventClientSide detour, so it does the
 * minimum: KV3 -> JSON, then hand off. The client's OfferGameEvent is a cheap
 * atomic check plus a bounded enqueue; the stream write happens on its own thread.
 */
void ForwardGameEvent(void *eventPtr)
{
    if (g_SaveKV3AsJSON == nullptr || eventPtr == nullptr || !grpcClient)
    {
        return;
    }

    auto *gameEvent = reinterpret_cast<IGameEventReader *>(eventPtr);
    const char *name = gameEvent->GetName();
    if (name == nullptr)
    {
        return;
    }

    CUtlString error;
    CUtlString output;
    if (g_SaveKV3AsJSON(gameEvent->GetDataKeys(), &error, &output))
    {
        grpcClient->OfferGameEvent(name, output.Get());
    }
}

bool NewFireEventClientSide(void *thisptr, void *event)
{
    ForwardGameEvent(event);
    return originalFireEventClientSide(thisptr, event);
}

/**
 * @brief Finds a class's vtable in a loaded module via its MSVC RTTI type name.
 *
 * Walks the MSVC RTTI chain the same way HLAE's Afx::BinUtils::FindClassVtable does:
 * the ".?AVCGameEventManager@@" type-descriptor string -> the descriptor ->
 * a base-class descriptor referencing it -> the base-class array -> the class
 * hierarchy descriptor -> the complete-object locator -> the vtable that points at
 * that locator. Restricted to single-inheritance classes with a zero offset (which
 * CGameEventManager is), which is what lets it collapse to this much shorter form.
 *
 * @return The vtable address, or nullptr when the class cannot be located (an
 *         obfuscated/changed build; the caller degrades gracefully).
 */
void **FindClassVtableByRtti(HMODULE module, const char *rttiTypeName)
{
    auto *base = reinterpret_cast<const uint8_t *>(module);
    auto *dosHeader = reinterpret_cast<const IMAGE_DOS_HEADER *>(base);
    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return nullptr;
    }
    auto *ntHeaders = reinterpret_cast<const IMAGE_NT_HEADERS64 *>(base + dosHeader->e_lfanew);
    if (ntHeaders->Signature != IMAGE_NT_SIGNATURE)
    {
        return nullptr;
    }

    const uint8_t *imageStart = base;
    const uint8_t *imageEnd = base + ntHeaders->OptionalHeader.SizeOfImage;
    const size_t nameLength = strlen(rttiTypeName);

    // 1. The RTTI type descriptor: {vftable, spare, ".?AVCGameEventManager@@"}.
    const uint8_t *typeDescriptor = nullptr;
    for (const uint8_t *p = imageStart; p + nameLength + 1 < imageEnd; ++p)
    {
        if (memcmp(p, rttiTypeName, nameLength + 1) == 0)
        {
            typeDescriptor = p - 2 * sizeof(void *);
            break;
        }
    }
    if (typeDescriptor == nullptr || typeDescriptor < imageStart)
    {
        return nullptr;
    }
    const DWORD typeDescriptorRva = static_cast<DWORD>(typeDescriptor - base);

    // 2. A complete-object locator referencing that descriptor, then 3. the vtable
    //    whose slot -1 points at the locator. Signature (x64 COL): {signature=1,
    //    offset=0, cdOffset=0, pTypeDescriptor, pClassDescriptor, pSelf}.
    for (const uint8_t *p = imageStart; p + 6 * sizeof(DWORD) < imageEnd; p += sizeof(DWORD))
    {
        auto *fields = reinterpret_cast<const DWORD *>(p);
        if (fields[0] != 1 || fields[1] != 0 || fields[2] != 0 || fields[3] != typeDescriptorRva)
        {
            continue;
        }

        const void *locator = p;
        for (const uint8_t *q = imageStart; q + 2 * sizeof(void *) < imageEnd; q += sizeof(void *))
        {
            if (*reinterpret_cast<const void *const *>(q) == locator)
            {
                return (void **)(q + sizeof(void *));
            }
        }
    }

    return nullptr;
}
} // namespace

bool CsvgGameEventTelemetrySupported()
{
    return true;
}

bool CsvgEnsureGameEventHookInstalled()
{
    if (originalFireEventClientSide != nullptr)
    {
        return true; // Already installed
    }

    HMODULE tier0 = GetModuleHandleA("tier0.dll");
    if (tier0 == nullptr)
    {
        Log("[GameEvents] tier0.dll not loaded - cannot resolve SaveKV3AsJSON");
        return false;
    }
    g_SaveKV3AsJSON = (SaveKV3AsJSONFn)GetProcAddress(tier0, "?SaveKV3AsJSON@@YA_NPEBVKeyValues3@@PEAVCUtlString@@1@Z");
    if (g_SaveKV3AsJSON == nullptr)
    {
        Log("[GameEvents] tier0!SaveKV3AsJSON not found - the export was renamed by a CS2 update");
        return false;
    }

    HMODULE clientDll = GetModuleHandleA("client.dll");
    if (clientDll == nullptr)
    {
        Log("[GameEvents] client.dll not loaded yet - enable telemetry after a demo/map has loaded");
        return false;
    }

    gameEventManagerVtable = FindClassVtableByRtti(clientDll, ".?AVCGameEventManager@@");
    if (gameEventManagerVtable == nullptr)
    {
        Log("[GameEvents] CGameEventManager vtable not found in client.dll (RTTI layout changed?)");
        return false;
    }

    originalFireEventClientSide = (FireEventClientSideFn)gameEventManagerVtable[kFireEventClientSideSlot];
    PatchVTableEntry(gameEventManagerVtable, kFireEventClientSideSlot, (void *)&NewFireEventClientSide);
    Log("[GameEvents] Hooked CGameEventManager::FireEventClientSide (slot %d)", kFireEventClientSideSlot);
    return true;
}
#else
// Linux: no game-event hook yet (see the note above). The capability is withheld,
// so the orchestrator never sends a subscription; if one arrives anyway (an
// out-of-date host), the install fails and the command is nacked.
bool CsvgGameEventTelemetrySupported()
{
    return false;
}

bool CsvgEnsureGameEventHookInstalled()
{
    Log("[GameEvents] Game-event telemetry is not supported on this platform (Windows-only)");
    return false;
}
#endif

/**
 * @brief Hooked Connect function that replaces Source2ServerConfig001::Connect.
 *
 * Called by the engine during server initialization. Delegates to the original
 * Connect, acquires the CVar interface, registers console commands, starts the
 * gRPC client, and restores the gameinfo.gi file.
 *
 * @param appSystem The app system being connected.
 * @param factoryFn The engine's interface factory.
 * @return Result from the original Connect function.
 */
bool Connect(IAppSystem *appSystem, CreateInterfaceFn factoryFn)
{
    factory = factoryFn;
    bool result = serverConfigConnect(appSystem, factory);

    g_pCVar = (ICvar *)factory("VEngineCvar007", NULL);
#ifdef CON_COMMAND_ENABLED
    ConVar_Register();
#endif

    // Since the Armory update `startmovie` ships FCVAR_HIDDEN, which makes it
    // undispatchable from ExecuteClientCmd - the InEngine capture provider depends on
    // this running before it ever issues one.
    UnhideCommandsAndCvars();

    // Opportunistic and, in practice, always too early: client.dll is not loaded yet at
    // this point, so this is expected to fail. The polling loop retries until it wins.
    if (!TryInstallFrameStageNotifyHook("Connect") && !connectSiteMissLogged.exchange(true))
    {
        Log("Source2Client002 not yet available at Connect (client.dll not loaded) - will retry from the polling "
            "loop; console commands stay queued until the FrameStageNotify hook installs");
    }

    StartGrpcClient();

    RestoreGameinfoFile();

    return result;
}

/**
 * @brief Hooked Shutdown function that replaces
 * Source2ServerConfig001::Shutdown.
 *
 * Signals the gRPC client to stop, joins the stream thread, unregisters
 * console commands, and delegates to the original Shutdown.
 */
void Shutdown()
{
    isQuitting = true;

    if (serverConfigShutdown != NULL)
    {
        serverConfigShutdown();
    }

#ifdef CON_COMMAND_ENABLED
    ConVar_Unregister();
#endif

    if (grpcClient)
    {
        grpcClient->Stop();
    }

    if (grpcThread.joinable())
    {
        grpcThread.join();
    }
}

/**
 * @brief Verifies that CS2 was launched with the -insecure parameter.
 *
 * The -insecure flag is required for plugin loading. If absent, this
 * function displays a fatal error and terminates the process. Iterates
 * command-line args manually because CommandLine()->HasParm crashes when
 * the parameter is missing (since the Armory update).
 */
void AssertInsecureParameterIsPresent()
{
    bool found = false;
    // Since the "Armory" update, calling CommandLine()->HasParm("-insecure")
    // crashes the game when the parameter is not present.
    auto parameters = CommandLine()->GetParms();
    for (int i = 0; i < CommandLine()->ParmCount(); i++)
    {
        if (strcmp(parameters[i], "-insecure") == 0)
        {
            found = true;
            break;
        }
    }

    if (!found)
    {
        PluginError("CS:DM plugin loaded without the -insecure launch "
                    "option.\n\nAborting.");
    }
}

/**
 * @brief Exported DLL entry point called by the CS2 engine to create
 * interfaces.
 *
 * The first call clears the old log, asserts the -insecure launch parameter, loads
 * the real server library (server.dll / libserver.so) and resolves its CreateInterface.
 *
 * On every call, it delegates to the real server's CreateInterface. When
 * the engine requests "Source2ServerConfig001", the plugin hooks the vtable
 * Connect (index 0) and Shutdown (index 4) entries to inject its own
 * initialization and cleanup logic.
 *
 * @param pName The interface name requested by the engine.
 * @param pReturnCode Optional return code (0 = success).
 * @return Pointer to the requested interface, or nullptr if not found.
 */
EXPORT void *CreateInterface(const char *pName, int *pReturnCode)
{
    if (serverCreateInterface == NULL)
    {
        DeleteLogFile();
        pluginLoadTime = std::chrono::steady_clock::now();
        AssertInsecureParameterIsPresent();

        const char *gameDirectory = Plat_GetGameDirectory();
        gameInfoPath = string(gameDirectory) + "/csgo/gameinfo.gi";
        gameInfoBackupPath = string(gameDirectory) + "/csgo/gameinfo.gi.backup";
        string libPath = string(gameDirectory) + SERVER_LIB_PATH;

        void *serverModule = LoadLib(libPath.c_str());
        if (serverModule == NULL)
        {
            PluginError("Could not load server lib %s : %s", libPath.c_str(), GetLastErrorString());
        }

        serverCreateInterface = (CreateInterfaceFn)GetLibAddress(serverModule, "CreateInterface");
        if (serverCreateInterface == NULL)
        {
            PluginError("Could not find CreateInterface : %s", GetLastErrorString());
        }
    }

    void *original = serverCreateInterface(pName, pReturnCode);
    if (original == nullptr)
    {
        return original;
    }

    auto vtable = *(void ***)original;
    if (strcmp(pName, "Source2ServerConfig001") == 0)
    {
        serverConfigConnect = (AppSystemConnectFn)vtable[0];
        serverConfigShutdown = (AppSystemShutdownFn)vtable[4];
        PatchVTableEntry(vtable, 0, (void *)&Connect);
        PatchVTableEntry(vtable, 4, (void *)&Shutdown);
    }
    else if (strcmp(pName, "Source2GameClients001") == 0)
    {
        // Entry point for the engine-thread frame hook: ClientFullyConnect is the
        // earliest place Source2Client002 can be resolved (see NewClientFullyConnect).
        originalClientFullyConnect = (ClientFullyConnectFn)vtable[kSource2GameClients_ClientFullyConnectSlot];
        PatchVTableEntry(vtable, kSource2GameClients_ClientFullyConnectSlot, (void *)&NewClientFullyConnect);
    }

    return original;
}

#ifdef CON_COMMAND_ENABLED
CON_COMMAND(csvg_info, "Displays CS:VG plugin information")
{
    Log("===Counter-Strike 2 Video Generator===");
    Log("Version: %f", 1.0);
}
#endif
