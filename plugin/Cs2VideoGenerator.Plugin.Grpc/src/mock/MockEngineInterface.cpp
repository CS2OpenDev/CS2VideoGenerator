/**
 * @file MockEngineInterface.cpp
 * @brief Implementation of MockEngineInterface
 */

#include "MockEngineInterface.h"

#include <cstdint>

MockEngineInterface::MockEngineInterface()
{
    // CSVG_MOCK_DEMO_TICKS: positive integer N = the mock demo naturally ends when
    // the tick counter reaches N (IsPlayingDemo() flips to false, exactly as a real
    // demo ending does). Unset/invalid = current behavior (ticks advance forever).
    // Read once here, before any thread exists, so m_demoEndTick needs no atomics.
    const char *demoTicksEnv = std::getenv("CSVG_MOCK_DEMO_TICKS");
    if (demoTicksEnv != nullptr)
    {
        int parsed = std::atoi(demoTicksEnv);
        if (parsed > 0)
        {
            m_demoEndTick = parsed;
            std::cout << "[MOCK ENGINE] CSVG_MOCK_DEMO_TICKS=" << parsed << " - demo will end naturally at tick "
                      << parsed << std::endl;
        }
        else
        {
            std::cout << "[MOCK ENGINE] Ignoring CSVG_MOCK_DEMO_TICKS (not a positive integer): " << demoTicksEnv
                      << std::endl;
        }
    }
}

MockEngineInterface::~MockEngineInterface()
{
    StopTickLoop();
}

void MockEngineInterface::StartTickLoop()
{
    if (m_tickThread.joinable())
    {
        return; // Already running
    }

    m_shouldStop = false;
    m_tickThread = std::thread([this]() { TickLoop(); });
    std::cout << "[MOCK ENGINE] - Tick loop started" << std::endl;
}

void MockEngineInterface::StopTickLoop()
{
    m_shouldStop = true;
    if (m_tickThread.joinable())
    {
        m_tickThread.join();
        std::cout << "[MOCK ENGINE] - Tick loop stopped" << std::endl;
    }
}

void MockEngineInterface::TickLoop()
{
    // CS2 runs at 64 ticks per second at demo_timescale 1.0. The interval is
    // recomputed every iteration from m_timescale so a timescale change (host
    // command or injected user action) is picked up mid-run. It is clamped to
    // [500 us, 250 ms] to avoid busy-spinning at extreme speed-ups and to keep
    // StopTickLoop's join responsive at extreme slow-downs.
    while (!m_shouldStop)
    {
        if (!m_isPaused && m_isPlayingDemo)
        {
            int tick = ++m_currentTick;
            // External polling will detect tick changes via GetDemoFile()->GetDemoTick()

            // CSVG_MOCK_DEMO_TICKS natural demo end: the engine just stops playing
            // (IsPlayingDemo() -> false) and tick advancement stops, exactly as a real
            // demo ending does; the client's polling loop then emits its
            // stop-transition DemoStateEvent. Atomics only - this thread must never
            // take m_commandMutex (ExecuteClientCmd("stop") joins this thread while
            // holding it). The demo file and loaded path deliberately stay untouched.
            if (m_demoEndTick > 0 && tick >= m_demoEndTick)
            {
                m_isPlayingDemo = false;
                std::cout << "[MOCK ENGINE] - Reached CSVG_MOCK_DEMO_TICKS tick " << m_demoEndTick
                          << " - natural demo end (IsPlayingDemo() now returns false)" << std::endl;
            }
        }

        const double timescale = static_cast<double>(m_timescale.load());
        auto intervalUs = static_cast<int64_t>(1000000.0 / (64.0 * timescale));
        if (intervalUs < 500)
        {
            intervalUs = 500;
        }
        else if (intervalUs > 250000)
        {
            intervalUs = 250000;
        }
        std::this_thread::sleep_for(std::chrono::microseconds(intervalUs));
    }
}

void MockEngineInterface::HandlePlayDemoCommand(const char *cmd)
{
    const char *pathStart = cmd + 9; // Skip "playdemo "
    while (*pathStart == ' ')
        pathStart++; // Skip leading spaces

    std::string demoPath = pathStart;
    if (!demoPath.empty() && demoPath.front() == '"')
    {
        demoPath = demoPath.substr(1);
    }
    if (!demoPath.empty() && demoPath.back() == '"')
    {
        demoPath.pop_back();
    }

    std::cout << "[MOCK ENGINE] CS2 Engine: playdemo " << demoPath << std::endl;

    if (m_isPlayingDemo.load())
    {
        // A playdemo while a demo is already playing first stops the current demo
        // (like the real engine). Flip the flag and give the client's snapshot reader a
        // cycle to (a) stop dereferencing the old MockDemoFile before it is replaced
        // below and (b) surface the stop->start transition it would observe against the
        // real engine. Still needed with the frame pump: the pump reads state on the
        // same thread that executes this command, so without the pause the stop->start
        // pair happens entirely between two of its snapshots and is never observed.
        m_isPlayingDemo = false;
        std::cout << "[MOCK ENGINE] - Stopping current demo before loading the new one" << std::endl;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }

    std::cout << "[MOCK ENGINE] - Opening demo file..." << std::endl;
    std::cout << "[MOCK ENGINE] - Parsing demo header..." << std::endl;

    m_loadedDemoPath = demoPath;
    m_currentTick = 0;
    m_isPaused = false; // Engine starts playing automatically

    m_demoFile = std::make_unique<MockDemoFile>(&m_currentTick, &m_isPaused);
    std::cout << "[MOCK ENGINE] - Demo file created" << std::endl;

    m_isPlayingDemo = true;

    StartTickLoop();

    std::cout << "[MOCK ENGINE] - Demo file opened, starting playback from tick 0" << std::endl;
    std::cout << "[MOCK ENGINE] - (IsPlayingDemo() now returns true)" << std::endl;

    // Simulate file I/O delay
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
}

void MockEngineInterface::HandleGotoTickCommand(const char *cmd)
{
    int tick = std::atoi(cmd + 14); // Skip "demo_gototick "

    std::cout << "[MOCK ENGINE] CS2 Engine: demo_gototick " << tick << std::endl;

    m_currentTick = tick;

    // External polling will detect tick change via GetDemoFile()->GetDemoTick()

    std::cout << "[MOCK ENGINE] - Jumped to tick " << tick << std::endl;
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
}

void MockEngineInterface::ExecuteClientCmd(int slot, const char *cmd, bool immediate)
{

    // Supress the warnings about unused parameters for slot and immediate
    (void)slot;
    (void)immediate;
    if (cmd == nullptr)
    {
        return;
    }

    // Serialize command execution: commands arrive from two writer threads - the gRPC
    // reader thread (host commands via the production Cs2PluginClient) and mock_server's
    // stdin injection thread (simulated user actions). Scalar state stays atomic for the
    // lock-free readers (tick thread, client polling thread); this mutex guards the
    // non-atomic state (demo path, demo file lifecycle, tick-thread start/stop,
    // spectator target) against concurrent writers. The tick thread never takes this
    // mutex (see TickLoop), so the "stop" handler can join it while holding the lock.
    std::lock_guard<std::mutex> lock(m_commandMutex);

    if (std::strncmp(cmd, "playdemo", 8) == 0)
    {
        HandlePlayDemoCommand(cmd);
    }
    else if (std::strcmp(cmd, "demo_pause") == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: demo_pause (tick: " << m_currentTick.load() << ")" << std::endl;
        m_isPaused = true;
        std::cout << "[MOCK ENGINE] - Playback paused at tick " << m_currentTick.load() << std::endl;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    else if (std::strcmp(cmd, "demo_resume") == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: demo_resume (tick: " << m_currentTick.load() << ")" << std::endl;
        m_isPaused = false;
        std::cout << "[MOCK ENGINE] - Playback resumed from tick " << m_currentTick.load() << std::endl;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    else if (std::strncmp(cmd, "demo_gototick", 13) == 0)
    {
        HandleGotoTickCommand(cmd);
    }
    else if (std::strncmp(cmd, "demo_timescale", 14) == 0)
    {
        HandleTimescaleCommand(cmd);
    }
    else if (std::strcmp(cmd, "stop") == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: stop (closing demo at tick: " << m_currentTick.load() << ")"
                  << std::endl;

        // Flip the playing flag first: the client's polling snapshot only dereferences
        // GetDemoFile() after seeing IsPlayingDemo() == true, so flipping before the
        // teardown below (plus the StopTickLoop join) keeps the demo file alive for
        // any in-flight snapshot read.
        m_isPlayingDemo = false;

        StopTickLoop();
        m_demoFile.reset();
        std::cout << "[MOCK ENGINE] - Demo file destroyed" << std::endl;

        m_isPaused = true;
        m_currentTick = 0;
        m_loadedDemoPath.clear();
        std::cout << "[MOCK ENGINE] - Demo closed, resources released" << std::endl;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    else if (std::strncmp(cmd, "spec_player", 11) == 0)
    {
        HandleSpecPlayerCommand(cmd);
    }
    // Demo options - these are cosmetic settings, just log them
    else if (std::strncmp(cmd, "spec_show_xray", 14) == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: " << cmd << std::endl;
    }
    else if (std::strncmp(cmd, "cl_demo_predict", 15) == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: " << cmd << " (true view)" << std::endl;
    }
    else if (std::strncmp(cmd, "cl_drawhud", 10) == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: " << cmd << std::endl;
    }
    else if (std::strncmp(cmd, "mp_display_kill_assists", 23) == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: " << cmd << std::endl;
    }
    else if (std::strncmp(cmd, "cl_draw_only_deathnotices", 25) == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: " << cmd << std::endl;
    }
    // Other common demo commands - log but don't warn ("demo_ui_mode" is the v1.1
    // interactive-demo-ui load flag - expected, not unknown)
    else if (std::strncmp(cmd, "sv_cheats", 9) == 0 || std::strncmp(cmd, "volume", 6) == 0 ||
             std::strncmp(cmd, "cl_hud_telemetry", 16) == 0 || std::strncmp(cmd, "cl_trueview_show_status", 23) == 0 ||
             std::strncmp(cmd, "r_show_build_info", 17) == 0 || std::strncmp(cmd, "spec_mode", 9) == 0 ||
             std::strncmp(cmd, "demo_ui_mode", 12) == 0 || std::strncmp(cmd, "demoui", 6) == 0)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: " << cmd << std::endl;
    }
    else
    {
        std::cout << "[MOCK ENGINE] Unknown command: " << cmd << std::endl;
    }
}

void MockEngineInterface::HandleTimescaleCommand(const char *cmd)
{
    const char *arg = cmd + 14; // Skip "demo_timescale"
    while (*arg == ' ')
        arg++;

    char *end = nullptr;
    float value = std::strtof(arg, &end);
    if (end == arg || !(value > 0.0f))
    {
        // Guard: a real demo_timescale of 0 or below would stop/reverse nothing useful;
        // ignore with a log line so tests can assert the rejection.
        std::cout << "[MOCK ENGINE] Ignoring demo_timescale with missing/non-positive value: " << cmd << std::endl;
        return;
    }

    m_timescale.store(value);
    const auto intervalUs = static_cast<int64_t>(1000000.0 / (64.0 * static_cast<double>(value)));
    std::cout << "[MOCK ENGINE] CS2 Engine: demo_timescale " << value << " (tick interval now ~" << intervalUs
              << " us; a running tick loop picks up the new rate)" << std::endl;
}

void MockEngineInterface::HandleSpecPlayerCommand(const char *cmd)
{
    // Production sends `spec_player "<display name>"` (name quoted to survive spaces).
    // Parse the quoted-or-unquoted name and store it as the current spectator target;
    // keep the legacy slot interpretation when the argument is purely numeric.
    const char *arg = cmd + 11; // Skip "spec_player"
    while (*arg == ' ')
        arg++;

    std::string target = arg;
    if (!target.empty() && target.front() == '"')
    {
        target.erase(0, 1);
    }
    if (!target.empty() && target.back() == '"')
    {
        target.pop_back();
    }

    if (target.empty())
    {
        std::cout << "[MOCK ENGINE] Ignoring spec_player with no target" << std::endl;
        return;
    }

    m_spectatorTarget = target;

    const bool numericSlot = target.find_first_not_of("0123456789") == std::string::npos;
    if (numericSlot)
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: spec_player " << target << " (spectating player slot " << target << ")"
                  << std::endl;
    }
    else
    {
        std::cout << "[MOCK ENGINE] CS2 Engine: spec_player \"" << target << "\" (spectating player \"" << target
                  << "\")" << std::endl;
    }
}

void MockEngineInterface::EndDemoNaturally()
{
    std::lock_guard<std::mutex> lock(m_commandMutex);

    if (!m_isPlayingDemo.load())
    {
        std::cout << "[MOCK ENGINE] end-demo ignored: no demo is playing" << std::endl;
        return;
    }

    // Same effect as the tick thread reaching CSVG_MOCK_DEMO_TICKS: the engine just
    // stops playing. Only atomics are flipped - the demo file object and the loaded
    // path stay untouched exactly like the in-tick-thread natural end (the client's
    // snapshot read never dereferences the demo file once IsPlayingDemo() is false),
    // and the idle tick thread keeps sleeping until the next playdemo/stop.
    m_isPlayingDemo = false;
    std::cout << "[MOCK ENGINE] - Demo ended (natural end) at tick " << m_currentTick.load()
              << " (IsPlayingDemo() now returns false)" << std::endl;
}

std::string MockEngineInterface::GetSpectatorTarget()
{
    std::lock_guard<std::mutex> lock(m_commandMutex);
    return m_spectatorTarget;
}

bool MockEngineInterface::IsPlayingDemo()
{
    return m_isPlayingDemo.load();
}
