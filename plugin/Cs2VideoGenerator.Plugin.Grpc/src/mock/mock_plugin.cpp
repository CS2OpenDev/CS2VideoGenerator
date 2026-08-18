/**
 * @file mock_plugin.cpp
 * @brief Mock CS2 plugin entry point for testing gRPC communication
 *
 * Stands up a MockEngineInterface, hands it to the production Cs2PluginClient, and
 * connects to the .NET Core server. Because the mocking happens at the engine command
 * level, everything above the engine is the same code a real session runs.
 *
 * Architecture mirrors the real CS2 plugin: the engine is stateful and knows nothing
 * about the client, and the client's own polling loop is what observes it
 * (GetDemoFile()->GetDemoTick(), IsPlayingDemo()).
 *
 * @par Command Line Arguments:
 * - --traceparent=VALUE: W3C trace context for distributed tracing
 *
 * @par Network Configuration:
 * Connects to gRPC server at 127.0.0.1:50051 using insecure credentials
 *
 * @par User-Action Injection Channel (stdin):
 * A reader thread accepts one command per line on stdin and drives the mock engine
 * directly, bypassing the gRPC command path, so the production client's polling
 * loop observes the changes exactly as real in-game user actions. See
 * InjectionReaderLoop for the grammar. EOF on stdin stops the reader thread without
 * exiting the process.
 */

#include "mock/MockEngineInterface.h"
#include "plugin/Cs2PluginClient.h"
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <grpcpp/grpcpp.h>
#include <iostream>
#include <memory>
#include <string>
#include <thread>

/**
 * @brief Mock implementation of the game version provider used by Cs2PluginClient.
 *
 * The mock has no running game, so by default it reports no game version
 * (the orchestrator treats this as "unknown"). Tests can simulate a specific
 * CS2 build by setting the CSVG_MOCK_GAME_VERSION environment variable, which
 * is inherited from the orchestrator process that launches mock_server.
 *
 * @return The simulated game version, or an empty string if not configured.
 */
std::string GetCsvgGameVersion()
{
    const char *simulated = std::getenv("CSVG_MOCK_GAME_VERSION");
    return simulated != nullptr ? std::string(simulated) : std::string();
}

/**
 * @brief Game-event telemetry support for the mock ("game-event-telemetry").
 *
 * The mock has no CGameEventManager to detour: synthetic events arrive over the
 * stdin injection channel (`game-event <name> [json]`), so the capability is always
 * available and the "lazy install" is a no-op that succeeds. The real plugin's
 * versions of these two functions live in server_plugin.cpp.
 */
bool CsvgGameEventTelemetrySupported()
{
    return true;
}

/**
 * @brief The mock has no renderer, so present capture is never supported and the
 *        command seam is a no-op. The real implementations live in present_capture.cpp.
 */
bool CsvgPresentCaptureSupported()
{
    return false;
}

void CsvgPresentCaptureOnEngineCommand(const char *)
{
}

bool CsvgEnsureGameEventHookInstalled()
{
    return true;
}

/// Set once in main (before the client is constructed) from CSVG_MOCK_FRAME_HZ.
/// Mirrors the real plugin's Source2Client002::FrameStageNotify patch state.
static std::atomic<bool> g_engineFramePumpEnabled{false};

bool CsvgEngineFrameHookInstalled()
{
    return g_engineFramePumpEnabled.load();
}

/**
 * @brief Hook-install retry for the mock: a no-op.
 *
 * The real plugin polls for Source2Client002 until client.dll is up; the mock's frame
 * pump stands in for the hook and is decided once in main from CSVG_MOCK_FRAME_HZ, so
 * there is nothing to retry - this just reports the pump's state.
 *
 * @return true when the mock frame pump is running.
 */
bool CsvgTryInstallEngineFrameHook()
{
    return g_engineFramePumpEnabled.load();
}

/**
 * @brief Execution discipline for the mock: direct execution stays allowed.
 *
 * The real plugin returns true (queue and wait - ExecuteClientCmd off the engine
 * thread crashes CS2). The mock has no such hazard, and `CSVG_MOCK_FRAME_HZ=0`
 * deliberately exercises the no-frame-pump leg where commands execute directly from
 * the calling thread; a .NET end-to-end test depends on it.
 *
 * @return false - always, on the mock.
 */
bool CsvgCommandsRequireEngineThread()
{
    return false;
}

namespace
{

/// Set when main is about to tear down; the injection reader must stop dispatching
/// into the (stack-allocated) mock engine. The reader thread is detached because it
/// blocks in getline with no portable way to interrupt it.
std::atomic<bool> g_injectionShutdown{false};

/**
 * @brief Strips leading/trailing whitespace (including the \r of CRLF line endings)
 */
std::string TrimWhitespace(const std::string &input)
{
    size_t begin = 0;
    size_t end = input.size();
    while (begin < end && std::isspace(static_cast<unsigned char>(input[begin])))
    {
        begin++;
    }
    while (end > begin && std::isspace(static_cast<unsigned char>(input[end - 1])))
    {
        end--;
    }
    return input.substr(begin, end - begin);
}

/**
 * @brief User-action injection channel: reads commands from stdin, one per line, and
 *        drives MockEngineInterface state directly, bypassing the gRPC command path.
 *
 * Because the production Cs2PluginClient only learns about these changes through its
 * ~120 Hz engine polling loop, injected actions are observed exactly like real user
 * actions in the CS2 console/demo UI: no command echo, no host-command suppression
 * stamp - so DemoStateEvent transitions they cause are attributed ORIGIN_USER.
 *
 * Grammar (one command per line; unknown lines are logged and ignored):
 * - `user-pause`            - demo_pause as the user (tick advancement stops; the
 *                             client's echo-truth is_paused is deliberately not touched)
 * - `user-resume`           - demo_resume as the user
 * - `user-seek <tick>`      - demo_gototick <tick> as the user (tick must be a
 *                             non-negative integer)
 * - `user-playdemo <path>`  - playdemo <path> as the user; the path need not exist
 *                             (console use bypasses the host client's exists-check
 *                             by design)
 * - `user-stop`             - stop as the user
 * - `user-timescale <v>`    - demo_timescale <v> as the user (engine ignores v <= 0)
 * - `end-demo`              - immediate natural demo end (same effect as reaching
 *                             CSVG_MOCK_DEMO_TICKS)
 * - `game-event <name> [json]` - a synthetic client-side game event, offered to the
 *                             plugin client's telemetry pipeline exactly as the real
 *                             plugin's FireEventClientSide detour does. Forwarded to
 *                             the host only while a subscription is active (the
 *                             mock's stand-in for the CGameEventManager hook); the
 *                             JSON payload defaults to `{}` when omitted.
 *
 * Nothing is written to stdout as a reply; the lines below are ordinary mock log
 * output on the existing log stream. EOF on stdin (headless/test runners may close
 * it) stops this thread without crashing or exiting the process.
 */
void InjectionReaderLoop(MockEngineInterface &engine, Cs2PluginClient &client)
{
    std::cout << "[MOCK INJECT] User-action injection channel ready (stdin, one command per line)" << std::endl;

    std::string line;
    while (std::getline(std::cin, line))
    {
        if (g_injectionShutdown.load())
        {
            break;
        }

        line = TrimWhitespace(line);
        if (line.empty())
        {
            continue;
        }

        std::string command = line;
        std::string argument;
        const size_t space = line.find(' ');
        if (space != std::string::npos)
        {
            command = line.substr(0, space);
            argument = TrimWhitespace(line.substr(space + 1));
        }

        if (command == "user-pause")
        {
            std::cout << "[MOCK INJECT] user-pause -> demo_pause (as user)" << std::endl;
            engine.ExecuteClientCmd(0, "demo_pause", true);
        }
        else if (command == "user-resume")
        {
            std::cout << "[MOCK INJECT] user-resume -> demo_resume (as user)" << std::endl;
            engine.ExecuteClientCmd(0, "demo_resume", true);
        }
        else if (command == "user-seek")
        {
            char *parseEnd = nullptr;
            const long tick = std::strtol(argument.c_str(), &parseEnd, 10);
            if (argument.empty() || parseEnd == argument.c_str() || *parseEnd != '\0' || tick < 0)
            {
                std::cout << "[MOCK INJECT] Ignoring user-seek with invalid tick: '" << argument << "'" << std::endl;
            }
            else
            {
                const std::string engineCmd = "demo_gototick " + std::to_string(tick);
                std::cout << "[MOCK INJECT] user-seek -> " << engineCmd << " (as user)" << std::endl;
                engine.ExecuteClientCmd(0, engineCmd.c_str(), true);
            }
        }
        else if (command == "user-playdemo")
        {
            if (argument.empty())
            {
                std::cout << "[MOCK INJECT] Ignoring user-playdemo with no path" << std::endl;
            }
            else
            {
                // The path need not exist: console playdemo bypasses the host client's
                // exists-check by design. The engine handler strips optional quotes.
                const std::string engineCmd = "playdemo " + argument;
                std::cout << "[MOCK INJECT] user-playdemo -> " << engineCmd << " (as user)" << std::endl;
                engine.ExecuteClientCmd(0, engineCmd.c_str(), true);
            }
        }
        else if (command == "user-stop")
        {
            std::cout << "[MOCK INJECT] user-stop -> stop (as user)" << std::endl;
            engine.ExecuteClientCmd(0, "stop", true);
        }
        else if (command == "user-timescale")
        {
            if (argument.empty())
            {
                std::cout << "[MOCK INJECT] Ignoring user-timescale with no value" << std::endl;
            }
            else
            {
                // Value validation (numeric, > 0) happens in the engine's
                // demo_timescale handler, matching console behavior.
                const std::string engineCmd = "demo_timescale " + argument;
                std::cout << "[MOCK INJECT] user-timescale -> " << engineCmd << " (as user)" << std::endl;
                engine.ExecuteClientCmd(0, engineCmd.c_str(), true);
            }
        }
        else if (command == "end-demo")
        {
            std::cout << "[MOCK INJECT] end-demo -> natural demo end" << std::endl;
            engine.EndDemoNaturally();
        }
        else if (command == "game-event")
        {
            // `game-event <name> [json]`. The name is the first token; everything after
            // it is the raw JSON payload (defaulting to "{}") - passed through
            // unvalidated, mirroring the real hook, which forwards whatever
            // SaveKV3AsJSON produced.
            std::string eventName = argument;
            std::string payload = "{}";
            const size_t payloadSplit = argument.find(' ');
            if (payloadSplit != std::string::npos)
            {
                eventName = argument.substr(0, payloadSplit);
                payload = TrimWhitespace(argument.substr(payloadSplit + 1));
            }

            if (eventName.empty())
            {
                std::cout << "[MOCK INJECT] Ignoring game-event with no event name" << std::endl;
            }
            else
            {
                std::cout << "[MOCK INJECT] game-event -> " << eventName << " " << payload << std::endl;
                client.OfferGameEvent(eventName.c_str(), payload.c_str());
            }
        }
        else
        {
            std::cout << "[MOCK INJECT] Unknown injection command (ignored): " << line << std::endl;
        }
    }

    std::cout << "[MOCK INJECT] Injection channel stopped (stdin EOF or shutdown)" << std::endl;
}

/**
 * @brief Stands in for CS2's engine thread: calls Cs2PluginClient::OnEngineFrame at a
 *        fixed cadence, exactly as the real plugin's patched
 *        Source2Client002::FrameStageNotify does at FRAME_START.
 *
 * With this pump the mock exercises the same production code path the real plugin
 * uses - queued console commands drain here, engine state is read here, and the
 * client's own polling thread stays passive while frames keep arriving. The default
 * 250 Hz is deliberately faster than the mock engine's 64 tick/s clock (a real game
 * renders far above tick rate too) and is overridable via CSVG_MOCK_FRAME_HZ; 0
 * disables the pump entirely, which is the regression harness for the direct-execute
 * fallback (frame hook never installed / vtable drift).
 */
void EngineFramePumpLoop(Cs2PluginClient &client, const std::atomic<bool> &shouldStop, int frameHz)
{
    const auto interval = std::chrono::microseconds(1000000 / frameHz);
    std::cout << "[MOCK FRAME] Engine frame pump started (" << frameHz << " Hz)" << std::endl;
    while (!shouldStop.load())
    {
        client.OnEngineFrame();
        std::this_thread::sleep_for(interval);
    }
    std::cout << "[MOCK FRAME] Engine frame pump stopped" << std::endl;
}

/// Parses CSVG_MOCK_FRAME_HZ (default 250; 0 = no pump, i.e. no frame hook at all).
int ResolveFramePumpHz()
{
    const char *value = std::getenv("CSVG_MOCK_FRAME_HZ");
    if (value == nullptr)
    {
        return 250;
    }
    char *parseEnd = nullptr;
    const long parsed = std::strtol(value, &parseEnd, 10);
    if (parseEnd == value || *parseEnd != '\0' || parsed < 0 || parsed > 10000)
    {
        std::cout << "[MOCK FRAME] Ignoring CSVG_MOCK_FRAME_HZ (not in 0..10000): " << value << std::endl;
        return 250;
    }
    return static_cast<int>(parsed);
}

} // namespace

/**
 * @brief Parse the --traceparent argument from command line
 * @param argc Number of arguments
 * @param argv Argument array
 * @return The traceparent value, or empty string if not found
 */
std::string parseTraceparent(int argc, char *argv[])
{
    const char *prefix = "--traceparent=";
    size_t prefixLen = strlen(prefix);

    for (int i = 1; i < argc; i++)
    {
        if (strncmp(argv[i], prefix, prefixLen) == 0)
        {
            return std::string(argv[i] + prefixLen);
        }
    }
    return "";
}

/**
 * @brief Main entry point for the mock plugin application
 *
 * Initializes the mock engine, establishes a gRPC connection, and starts a game
 * session that blocks until the server closes the connection.
 *
 * @param argc Number of command-line arguments
 * @param argv Array of command-line argument strings
 * @return 0 on success, 1 on error
 *
 * @par Error Handling:
 * - Returns 1 if connection fails within 10 seconds
 * - Returns 1 if an exception occurs during execution
 * - All errors are logged to std::cerr
 *
 * @par Example Usage:
 * @code
 * // Run the mock plugin
 * ./mock_server
 *
 * // Run with distributed tracing
 * ./mock_server --traceparent=00-abc123-def456-01
 * @endcode
 */
int main(int argc, char *argv[])
{
    std::cout << "Program Name: " << argv[0] << std::endl;
    std::cout << "Number of arguments (including program name): " << argc << std::endl;

    std::string traceparent = parseTraceparent(argc, argv);
    if (!traceparent.empty())
    {
        std::cout << "Traceparent: " << traceparent << std::endl;
    }

    if (argc > 1)
    {
        std::cout << "\n--- Command-line arguments passed ---" << std::endl;
        for (int i = 1; i < argc; i++)
        {
            std::cout << "Argument " << i << ": " << argv[i] << std::endl;
        }
    }
    else
    {
        std::cout << "\nNo additional command-line arguments provided." << std::endl;
    }

    std::cout << "Starting gRPC Client..." << std::endl;

    auto channel = grpc::CreateChannel("127.0.0.1:50051", grpc::InsecureChannelCredentials());

    // Publish the frame-hook state before constructing the client: its polling thread
    // starts in the constructor and consults CsvgEngineFrameHookInstalled immediately.
    const int framePumpHz = ResolveFramePumpHz();
    g_engineFramePumpEnabled.store(framePumpHz > 0);

    MockEngineInterface mockEngine;

    Cs2PluginClient client(channel, [&mockEngine]() -> ISource2EngineToClient * { return &mockEngine; }, traceparent);

    std::cout << "Waiting for server connection..." << std::endl;
    if (!channel->WaitForConnected(std::chrono::system_clock::now() + std::chrono::seconds(10)))
    {
        std::cerr << "Failed to connect to server within 10 seconds" << std::endl;
        return 1;
    }

    std::cout << "Connected to gRPC server!" << std::endl;

    // The engine frame pump is the mock's stand-in for CS2's engine thread calling
    // FrameStageNotify. It must be running before any command arrives so queued console
    // commands are actually drained (see EngineFramePumpLoop).
    std::atomic<bool> stopFramePump{false};
    std::thread framePumpThread;
    if (framePumpHz > 0)
    {
        framePumpThread = std::thread(
            [&client, &stopFramePump, framePumpHz]() { EngineFramePumpLoop(client, stopFramePump, framePumpHz); });
    }
    else
    {
        std::cout << "[MOCK FRAME] Engine frame pump DISABLED (CSVG_MOCK_FRAME_HZ=0) - the client will "
                     "fall back to direct command execution and polling-thread snapshots"
                  << std::endl;
    }

    // Start the user-action injection channel now that the client is up (its polling
    // thread started in the constructor). Detached: the thread blocks in getline and
    // exits on stdin EOF; g_injectionShutdown stops it from dispatching into the
    // stack-allocated engine during teardown when stdin stays open (interactive use).
    std::thread([&mockEngine, &client]() { InjectionReaderLoop(mockEngine, client); }).detach();

    int exitCode = 0;
    try
    {
        // Blocks until the server closes the stream.
        client.StartGameSession();
    }
    catch (const std::exception &e)
    {
        std::cerr << "Exception occurred: " << e.what() << std::endl;
        exitCode = 1;
    }

    // Stop the injection reader from dispatching before main's locals are destroyed;
    // the grace sleep lets an in-flight injected command drain. A reader still blocked
    // in getline simply dies with the process.
    g_injectionShutdown = true;
    std::this_thread::sleep_for(std::chrono::milliseconds(100));

    // Join the frame pump before main's locals go away: it dereferences both the
    // client and (through it) the stack-allocated mock engine every frame.
    stopFramePump = true;
    if (framePumpThread.joinable())
    {
        framePumpThread.join();
    }

    // Client destructor will stop the polling thread automatically
    return exitCode;
}
