/**
 * @file MockEngineInterface.h
 * @brief Mock implementation of ISource2EngineToClient for testing
 *
 * This class simulates CS2 Source 2 engine behavior without requiring the actual game.
 * It parses console commands and maintains internal state to mimic engine behavior.
 *
 * Mocking happens at the engine command level, so the production client's own logic
 * (scheduled actions, status callbacks) runs against it exactly as it does in a real
 * session.
 */

#pragma once

#include "cdll_interfaces.h"
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

/**
 * @class MockDemoFile
 * @brief Mock implementation of IDemoFile for testing
 *
 * Only the methods the plugin actually reads are overridden; every other IDemoFile
 * slot uses the non-pure default bodies declared in cdll_interfaces.h.
 */
class MockDemoFile : public IDemoFile
{
  private:
    std::atomic<int> *m_currentTick; ///< Reference to engine's tick counter
    std::atomic<bool> *m_isPaused;   ///< Reference to engine's pause state

  public:
    MockDemoFile(std::atomic<int> *tick, std::atomic<bool> *paused) : m_currentTick(tick), m_isPaused(paused)
    {
    }

    virtual ~MockDemoFile() = default;

    int GetDemoTick() override
    {
        return m_currentTick->load();
    }
    bool IsPlayingDemo() override
    {
        return true;
    } // If demo file exists, it's "playing"
    bool IsDemoPaused() override
    {
        return m_isPaused->load();
    }
};

/**
 * @class MockEngineInterface
 * @brief Mock CS2 engine used by mock_server
 *
 * This class simulates the behavior of ISource2EngineToClient by:
 * - Parsing console commands (playdemo, demo_pause, demo_resume, demo_gototick,
 *   demo_timescale, stop, spec_player)
 * - Maintaining demo playback state (isPlaying, isPaused, currentTick, timescale)
 * - Running a tick loop at 64 * demo_timescale ticks per second
 * - Providing state via GetDemoFile()->GetDemoTick() (just like real engine)
 * - Optionally ending the demo "naturally" at CSVG_MOCK_DEMO_TICKS or via
 *   EndDemoNaturally() (IsPlayingDemo() flips to false, as a real demo end does)
 *
 * Like the real engine it is stateful and callback-free: it holds no reference back to
 * the plugin client, and an external polling loop is what observes its state.
 *
 * Only the methods the plugin actually calls are overridden; every other
 * ISource2EngineToClient slot uses the non-pure default bodies declared in
 * cdll_interfaces.h.
 *
 * @par Thread safety:
 * Commands arrive from two writer threads - the gRPC reader thread (host commands via
 * the production Cs2PluginClient) and mock_server's stdin injection thread (simulated
 * user actions). Scalar state stays atomic for the lock-free readers (the tick thread
 * and the client's ~120 Hz polling thread); m_commandMutex serializes the writers and
 * guards the non-atomic state (demo path, demo file lifecycle, tick-thread start/stop,
 * spectator target). The tick thread never takes m_commandMutex: the "stop" handler
 * joins it while holding the lock, so a natural demo end from inside the tick loop
 * flips atomics only.
 */
class MockEngineInterface : public ISource2EngineToClient
{
  private:
    std::atomic<bool> m_isPlayingDemo{false}; ///< Whether a demo is loaded
    std::atomic<bool> m_isPaused{true};       ///< Whether playback is paused
    std::atomic<int> m_currentTick{0};        ///< Current demo tick
    std::atomic<bool> m_shouldStop{false};    ///< Signal to stop tick thread
    std::atomic<float> m_timescale{1.0f};     ///< demo_timescale value (always > 0; scales the tick interval)
    std::thread m_tickThread;                 ///< Background thread for tick updates (only runs when demo loaded)
    std::string m_loadedDemoPath;             ///< Path to currently loaded demo
    std::string m_spectatorTarget;            ///< Current spec_player target (display name, or numeric slot as string)
    std::unique_ptr<MockDemoFile> m_demoFile; ///< Mock demo file (nullptr until demo is loaded)
    std::mutex m_commandMutex;                ///< Serializes command execution across writer threads (see class doc)
    int m_demoEndTick{0}; ///< CSVG_MOCK_DEMO_TICKS natural-end tick (0 = never ends); set once in ctor, read-only after

    /**
     * @brief Internal tick loop running at 64 * demo_timescale ticks per second
     *
     * This simulates the CS2 engine's tick updates. When not paused, it:
     * - Increments m_currentTick
     * - Updates state that can be polled via GetDemoFile()->GetDemoTick()
     * - Ends the demo naturally when m_demoEndTick (CSVG_MOCK_DEMO_TICKS) is reached
     *
     * The sleep interval is recomputed every iteration from m_timescale, so a
     * demo_timescale change is picked up mid-run.
     *
     * Notifies nobody: external polling is what detects the changes. Only advances
     * while a demo is playing (m_isPlayingDemo == true). Never takes m_commandMutex
     * (the "stop" handler joins this thread while holding it), so the natural demo end
     * flips atomics only.
     */
    void TickLoop();

    /** @brief Starts the tick loop thread. Called when a demo begins loading. */
    void StartTickLoop();

    /** @brief Stops and joins the tick loop thread. Called when a demo is stopped. */
    void StopTickLoop();

    /**
     * @brief Parses and executes a "playdemo <path>" command
     * @param cmd Full command string
     *
     * Playback starts unpaused at tick 0, as the real engine does; external polling
     * picks the transition up from IsPlayingDemo().
     */
    void HandlePlayDemoCommand(const char *cmd);

    /**
     * @brief Parses and executes a "demo_gototick <tick>" command
     * @param cmd Full command string
     */
    void HandleGotoTickCommand(const char *cmd);

    /**
     * @brief Parses and executes a "demo_timescale <value>" command
     * @param cmd Full command string
     *
     * Stores the value into m_timescale so the running tick loop picks up the new
     * rate (interval = 1 s / (64 * timescale)). Missing or non-positive values are
     * ignored with a log line.
     */
    void HandleTimescaleCommand(const char *cmd);

    /**
     * @brief Parses and executes a "spec_player <target>" command
     * @param cmd Full command string
     *
     * Production sends `spec_player "<display name>"` (quoted). The quoted-or-unquoted
     * name is parsed and stored as the current spectator target; a purely numeric
     * argument is kept as the legacy slot interpretation (logged as a slot).
     */
    void HandleSpecPlayerCommand(const char *cmd);

  public:
    /**
     * @brief Constructs mock engine
     *
     * Tick thread is not started until a demo is loaded via playdemo command.
     * Reads CSVG_MOCK_DEMO_TICKS (positive integer N = the demo naturally ends at
     * tick N; unset/invalid = ticks advance forever) once, before any thread exists.
     */
    MockEngineInterface();

    /**
     * @brief Destructor - stops tick thread and waits for it to finish
     */
    ~MockEngineInterface();

    /**
     * @brief Executes a console command (mock implementation)
     * @param slot Client slot (ignored in mock)
     * @param cmd Console command string
     * @param immediate Whether to execute immediately (ignored in mock)
     *
     * Parses cmd and dispatches to appropriate handler:
     * - "playdemo <path>" -> HandlePlayDemoCommand
     * - "demo_pause" -> m_isPaused = true
     * - "demo_resume" -> m_isPaused = false
     * - "demo_gototick <tick>" -> HandleGotoTickCommand
     * - "demo_timescale <v>" -> HandleTimescaleCommand
     * - "stop" -> closes the demo
     * - "spec_player <target>" -> HandleSpecPlayerCommand
     *
     * Serialized by m_commandMutex: callable from both the gRPC reader thread and
     * the stdin injection thread (which uses it to simulate console-typed commands).
     */
    void ExecuteClientCmd(int slot, const char *cmd, bool immediate) override;

    /**
     * @brief Immediately ends the demo as a natural end, not a host stop
     *
     * Same effect as the tick thread reaching CSVG_MOCK_DEMO_TICKS: IsPlayingDemo()
     * flips to false and tick advancement stops, exactly as a real demo ending does,
     * so the production client's polling loop emits its stop-transition
     * DemoStateEvent. Only atomics are flipped - the demo file object and the loaded
     * path stay untouched (matching the in-tick-thread natural end), and the idle
     * tick thread keeps sleeping until the next playdemo/stop.
     *
     * Called by mock_server's stdin injection channel ("end-demo"). No-op with a log
     * line when no demo is playing.
     */
    void EndDemoNaturally();

    /**
     * @brief Checks if demo is loaded and playing
     * @return true if demo is currently loaded (even if paused)
     * @note Not const because ISource2EngineToClient::IsPlayingDemo() is not const
     */
    bool IsPlayingDemo() override;

    /**
     * @brief Returns the currently loaded demo path ("" when none)
     *
     * Mirrors the real engine's slot-043 GetDemoFilePath over the stored playdemo
     * path, making the mock ready for demo identity with no further work.
     * After a natural demo end the path of the just-ended demo remains readable
     * until the next playdemo/stop (the natural end flips atomics only - see
     * EndDemoNaturally); an explicit "stop" clears it to "".
     *
     * @warning Returns an interior pointer into a string that is mutated under
     *          m_commandMutex; like the real engine, a caller racing a concurrent
     *          load/stop can observe a torn value. The production client reads this
     *          slot per polling cycle with an immediate bounded copy inside
     *          its guarded snapshot read - the torn-read window mirrors the real
     *          engine's.
     */
    const char *GetDemoFilePath() override
    {
        return m_loadedDemoPath.c_str();
    }

    /**
     * @brief Returns a copy of the current spectator target ("" when never set)
     *
     * The target is whatever the last spec_player command carried: a display name
     * (production sends `spec_player "<name>"`), or a numeric slot as a string.
     */
    std::string GetSpectatorTarget();

    /**
     * @brief Returns the mock demo file (nullptr until a demo is loaded)
     */
    IDemoFile *GetDemoFile() override
    {
        return m_demoFile.get();
    }
};
