/**
 * @file Cs2PluginClient.cpp
 * @brief Implementation of the CS2 game plugin gRPC client
 */

#include "plugin/Cs2PluginClient.h"
#include "cdll_interfaces.h"
#include "cs2-video-generator.grpc.pb.h"
#include "cs2-video-generator.pb.h"
#include "plugin_version.h"
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <filesystem>
#include <iostream>
#include <system_error>
#include <vector>

// Defined in server_plugin.cpp / mock_log.cpp.
void Log(const char *msg, ...);

// Game version provider, defined per binary: server_plugin.cpp reads steam.inf
// ClientVersion from the game directory, mock_plugin.cpp returns the
// CSVG_MOCK_GAME_VERSION environment variable if set. Empty string when the game
// version cannot be determined.
std::string GetCsvgGameVersion();

// Game-event telemetry hook, defined per binary ("game-event-telemetry"):
// - server_plugin.cpp: Windows locates the CGameEventManager vtable via RTTI and
//   patches FireEventClientSide; Linux reports unsupported (see the capability note
//   in SendProcessStatus).
// - mock_plugin.cpp: no hook needed - synthetic events arrive via the stdin
//   injection channel - so "install" trivially succeeds.
// CsvgGameEventTelemetrySupported gates the capability advertisement;
// CsvgEnsureGameEventHookInstalled performs the lazy install on first enable (the
// hard requirement: never detour at plugin load) and returns false on failure.
bool CsvgGameEventTelemetrySupported();

// present_capture.cpp: whether this build has the DXGI present hook (Windows real
// plugin only) - decides whether "present-capture" is advertised.
bool CsvgPresentCaptureSupported();
bool CsvgEnsureGameEventHookInstalled();

// Whether the host binary has installed the engine-thread frame hook, i.e. whether
// OnEngineFrame is (or will be) called. Sticky once true. Defined per binary:
// server_plugin.cpp reports the Source2Client002::FrameStageNotify patch state,
// mock_plugin.cpp reports whether its frame pump is running. This, not frame recency,
// decides whether console commands are queued for the engine thread and whether the
// polling thread reads engine state; see the header for why.
bool CsvgEngineFrameHookInstalled();

// Retry the engine-thread frame hook install, also defined per binary. The real
// plugin cannot install its hook at load time (client.dll is not up yet) and cannot
// wait for a gameplay callback either (ClientFullyConnect does not fire at the main
// menu, and reaching it requires executing `playdemo` - the very command that is
// unsafe unhooked), so the polling loop calls this every cycle until it succeeds:
// availability-driven installation, as HLAE's AfxHookSource2 does. The mock's
// implementation is a no-op reporting its frame pump.
bool CsvgTryInstallEngineFrameHook();

// Whether console commands must run on the engine thread on this binary. True on the
// real plugin: ISource2EngineToClient::ExecuteClientCmd from a gRPC background thread
// into a mid-load engine crashes CS2 deterministically, so a command with no hook yet
// waits in the queue instead of taking a "legacy" direct call. False on the mock,
// where CSVG_MOCK_FRAME_HZ=0 deliberately exercises the direct-execution leg.
bool CsvgCommandsRequireEngineThread();

Cs2PluginClient::Cs2PluginClient(std::shared_ptr<grpc::Channel> channel, EngineGetterFunc engineGetter,
                                 const std::string &traceparent)
    : stub_(cs2_video_generator::Cs2GameService::NewStub(channel)), channel_(channel), traceparent_(traceparent),
      stream_(nullptr), m_engineGetter(std::move(engineGetter))
{
    StartPolling();
}

Cs2PluginClient::~Cs2PluginClient()
{
    StopPolling();
    StopGameEventSender();
}

void Cs2PluginClient::StartGameSession()
{
    if (!traceparent_.empty())
    {
        context_.AddMetadata("traceparent", traceparent_);
        std::cout << "Added traceparent to gRPC metadata: " << traceparent_ << std::endl;
    }

    std::unique_ptr<
        grpc::ClientReaderWriter<cs2_video_generator::CsProcessEvent, cs2_video_generator::CsControlRequest>>
        stream(stub_->CreateGameSession(&context_));

    if (!stream)
    {
        std::cerr << "Failed to create game session stream" << std::endl;
        return;
    }

    stream_ = stream.get();

    std::cout << "Successfully established bidirectional gRPC stream" << std::endl;

    SendProcessStatus(cs2_video_generator::Cs2ProcessStatus_ProcessStatus_INITIALIZING);

    // Event-driven READY: the engine is demonstrably alive once a state snapshot has
    // been read successfully and the frame hook has observed an engine frame. The
    // previous blind 5 s sleep survives only as a fallback cap for configurations
    // where the frame signal cannot arrive yet (real CS2 before the polling loop has
    // installed the FrameStageNotify hook, and possibly until a map/demo loads -
    // whether FrameStageNotify fires at the main menu is not yet confirmed).
    std::thread ready_notifier([this]() {
        constexpr auto pollInterval = std::chrono::milliseconds(25);
        const auto fallbackDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        while (std::chrono::steady_clock::now() < fallbackDeadline)
        {
            if (m_firstSnapshotOk.load() && m_engineFrameCount.load() > 0)
            {
                Log("[gRPC] READY (engine snapshot read + engine frame observed)");
                SendProcessStatus(cs2_video_generator::Cs2ProcessStatus_ProcessStatus_READY);
                return;
            }
            std::this_thread::sleep_for(pollInterval);
        }
        Log("[gRPC] READY (fallback cap reached before engine readiness signals)");
        SendProcessStatus(cs2_video_generator::Cs2ProcessStatus_ProcessStatus_READY);
    });
    ready_notifier.detach();

    std::thread reader_thread([&stream, this]() {
        cs2_video_generator::CsControlRequest request;
        while (stream->Read(&request))
        {
            HandleControlRequest(request);
        }
        std::cout << "Server closed the stream" << std::endl;
    });

    // Send initial connection event (force=true to always send, and updates m_lastSentTick)
    SendTickUpdateIfNeeded(m_currentTick.load(), true);

    std::cout << "Sent initial connection event" << std::endl;

    reader_thread.join();

    // Block further writes and take the write lock before Finish(): the polling thread or a
    // command handler may still be mid-write, and Finish() is itself a write-side operation on
    // the completion queue. Running Finish() concurrently with a Write() (or WritesDone) trips
    // a gRPC CHECK (grpc_cq_begin_op) and aborts the process, so they must be serialized.
    m_streamClosing.store(true);
    grpc::Status status;
    {
        std::lock_guard<std::mutex> lock(m_streamMutex);
        status = stream->Finish();
    }
    if (!status.ok())
    {
        std::cerr << "Stream finished with error: " << status.error_message() << std::endl;
    }
}

void Cs2PluginClient::Stop()
{
    // Stop polling thread first (removes one writer before we tear the stream down).
    StopPolling();
    StopGameEventSender();

    // Block any further writes, then break the blocking Read so StartGameSession can run its
    // Finish() and return. We deliberately do not call WritesDone()/Finish() here: those are
    // write-side terminal ops owned by the StartGameSession thread, and issuing them from this
    // thread races that Finish() on the completion queue (the gRPC grpc_cq_begin_op CHECK).
    // TryCancel() is thread-safe and is all that's needed to unblock the read.
    m_streamClosing.store(true);
    context_.TryCancel();
}

bool Cs2PluginClient::WriteEvent(const cs2_video_generator::CsProcessEvent &event)
{
    std::lock_guard<std::mutex> lock(m_streamMutex);
    if (stream_ == nullptr || m_streamClosing.load())
    {
        return false; // Stream not ready yet, or shutting down.
    }
    return stream_->Write(event);
}

void Cs2PluginClient::SendTickUpdate(int currentTick, bool isPaused)
{
    cs2_video_generator::CsProcessEvent event;
    cs2_video_generator::TickUpdateEvent *tick_update = event.mutable_tick_update();
    tick_update->set_current_tick(currentTick);
    // Pause state on every tick update ("engine-pause-detection"): callers pass
    // EffectivePausedState() - polled IDemoFile::IsDemoPaused engine truth once the
    // polling thread has a valid read for the current demo, echo truth (last
    // host-commanded state) until then.
    tick_update->set_is_paused(isPaused);

    if (!WriteEvent(event))
    {
        std::cerr << "Failed to send tick update (stream unavailable or closing)" << std::endl;
        return;
    }

    std::cout << "Sent tick update: " << currentTick << " (paused: " << (isPaused ? "yes" : "no") << ")" << std::endl;
}

void Cs2PluginClient::SendPlaybackStatusChange(int newStatus, const std::string &errorMessage)
{
    int previousStatus = m_playbackStatus.load();

    cs2_video_generator::CsProcessEvent event;
    cs2_video_generator::DemoPlaybackStatusChange *status_change = event.mutable_demo_playback_status_change();
    status_change->set_previous_status(
        static_cast<cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus>(previousStatus));
    status_change->set_new_status(
        static_cast<cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus>(newStatus));

    if (!errorMessage.empty())
    {
        status_change->set_error_message(errorMessage);
    }

    if (!WriteEvent(event))
    {
        std::cerr << "Failed to send playback status change (stream unavailable or closing)" << std::endl;
        return;
    }

    std::cout << "Sent playback status change: "
              << cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus_Name(
                     static_cast<cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus>(previousStatus))
              << " -> "
              << cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus_Name(
                     static_cast<cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus>(newStatus))
              << std::endl;

    m_playbackStatus.store(newStatus);
}

void Cs2PluginClient::SendProcessStatus(cs2_video_generator::Cs2ProcessStatus_ProcessStatus status,
                                        const std::string &errorMessage)
{
    cs2_video_generator::CsProcessEvent event;
    auto *process_status = event.mutable_process_status();
    process_status->set_status(status);
    if (!errorMessage.empty())
    {
        process_status->set_error_message(errorMessage);
    }

    // Version handshake: report the plugin version and HL2SDK commit embedded
    // at build time, plus the running game version when it can be determined. The
    // orchestrator warns (never blocks) on unknown/newer versions.
    process_status->set_plugin_version(CSVG_PLUGIN_VERSION);
    process_status->set_hl2sdk_commit(CSVG_HL2SDK_COMMIT);
    std::string gameVersion = GetCsvgGameVersion();
    if (!gameVersion.empty())
    {
        process_status->set_game_version(gameVersion);
    }

    // Capability tokens: build-time constant list advertised in every process
    // status so the orchestrator can degrade per-token - warn, never error, matching
    // the version-handshake philosophy above. The only known tokens deliberately not
    // advertised by this build are "spectate-by-steamid" (pending empirical
    // spec_lock_to_accountid validation) and "spectator-report" (no spectator engine
    // read exists yet).
    static const char *const kCapabilities[] = {
        "command-ack",            // CommandAckEvent for command_id-carrying requests
        "timescale-set",          // SetDemoTimescaleCommand
        "load-failure-detection", // DEMO_FILE_NOT_FOUND / DEMO_FILE_UNPLAYABLE are emitted
        "user-demo-ui",           // LoadDemoCommand.interactive_demo_ui honored
        "demo-state-events",      // DemoStateEvent emission + QueryDemoStateCommand
        "seek-ack", // Arrival-verified SetDemoTick acks (observed_demo_tick) + pause_after_seek at arrival
        "engine-pause-detection",  // is_paused fields carry polled IDemoFile::IsDemoPaused truth
        "demo-identity",           // DemoStateEvent.demo_file_path population + path-change events
        "tick-scheduled-commands", // ScheduleCommandsAtTickCommand / ClearScheduledCommandsCommand
        "console-exec",            // ExecuteConsoleCommandsCommand (immediate engine-thread execution)
    };
    for (const char *capability : kCapabilities)
    {
        process_status->add_capabilities(capability);
    }
    // Advertised only where a game-event hook exists: the mock (synthetic events via
    // the injection channel) and the Windows real plugin (CGameEventManager vtable
    // patch). The Linux real plugin has no hook yet, so the token is withheld and the
    // orchestrator's SetGameEventSubscriptionAsync throws NotSupportedException there.
    if (CsvgGameEventTelemetrySupported())
    {
        process_status->add_capabilities("game-event-telemetry");
    }

    // Advertised only where the DXGI present hook exists (the Windows real plugin).
    // The mock has no renderer and the Linux plugin has no Vulkan analog yet, so the
    // orchestrator's InEngineHooked provider fails fast against them by this token's
    // absence rather than by a clip that never receives frames.
    if (CsvgPresentCaptureSupported())
    {
        process_status->add_capabilities("present-capture");
    }

    if (!WriteEvent(event))
    {
        std::cerr << "Failed to send process status update (stream unavailable or closing)" << std::endl;
    }
}

void Cs2PluginClient::SendCommandAck(uint64_t commandId, bool success, const std::string &errorMessage,
                                     int observedDemoTick)
{
    cs2_video_generator::CsProcessEvent event;
    cs2_video_generator::CommandAckEvent *ack = event.mutable_command_ack();
    ack->set_command_id(commandId);
    ack->set_success(success);
    if (!errorMessage.empty())
    {
        ack->set_error_message(errorMessage);
    }
    // Arrival-verified seeks ("seek-ack") report the tick the engine was actually
    // observed at on completion; negative = not a tick-verified command, omit.
    if (observedDemoTick >= 0)
    {
        ack->set_observed_demo_tick(observedDemoTick);
    }

    if (!WriteEvent(event))
    {
        std::cerr << "Failed to send command ack (stream unavailable or closing)" << std::endl;
        return;
    }

    std::cout << "Sent command ack: id=" << commandId << " success=" << (success ? "yes" : "no") << std::endl;
}

void Cs2PluginClient::SendDemoStateEvent(bool isPlayingDemo, int demoTick,
                                         cs2_video_generator::DemoStateEvent_ChangeOrigin origin,
                                         uint64_t inReplyToCommandId)
{
    cs2_video_generator::CsProcessEvent event;
    cs2_video_generator::DemoStateEvent *state = event.mutable_demo_state();
    state->set_is_playing_demo(isPlayingDemo);
    // Engine truth once the polling thread has a valid IDemoFile::IsDemoPaused read
    // for the current demo ("engine-pause-detection"); echo truth (last
    // host-commanded state) as fallback.
    state->set_is_paused(EffectivePausedState());
    if (demoTick >= 0)
    {
        state->set_demo_tick(demoTick);
    }
    // Demo identity ("demo-identity"): the last observed GetDemoFilePath copy. Always
    // set - this build reads the slot - with empty string = no demo (or an engine
    // build that returns null; the orchestrator falls back to IsPlayingDemo either way).
    state->set_demo_file_path(ObservedDemoPath());
    if (m_timescaleSet.load())
    {
        state->set_demo_timescale(m_demoTimescale.load());
    }
    state->set_origin(origin);
    if (inReplyToCommandId != 0)
    {
        state->set_in_reply_to_command_id(inReplyToCommandId);
    }

    if (!WriteEvent(event))
    {
        std::cerr << "Failed to send demo state event (stream unavailable or closing)" << std::endl;
        return;
    }

    std::cout << "Sent demo state event: playing=" << (isPlayingDemo ? "yes" : "no") << " tick=" << demoTick
              << " origin=" << cs2_video_generator::DemoStateEvent_ChangeOrigin_Name(origin) << std::endl;
}

void Cs2PluginClient::NotePlaybackCommand()
{
    m_lastPlaybackCommandNs.store(
        std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
            .count());
}

bool Cs2PluginClient::IsWithinHostCommandWindow() const
{
    int64_t lastNs = m_lastPlaybackCommandNs.load();
    if (lastNs == 0)
    {
        return false; // No playback-affecting command has executed yet
    }
    int64_t nowNs =
        std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
            .count();
    return (nowNs - lastNs) <= kHostCommandWindow.count();
}

bool Cs2PluginClient::EffectivePausedState() const
{
    return m_enginePausedValid.load() ? m_enginePaused.load() : m_isPaused.load();
}

std::string Cs2PluginClient::ObservedDemoPath()
{
    std::lock_guard<std::mutex> lock(m_observedPathMutex);
    return m_observedDemoPath;
}

bool Cs2PluginClient::UpdateObservedDemoPath(const char *path)
{
    std::lock_guard<std::mutex> lock(m_observedPathMutex);
    if (m_observedDemoPath == path)
    {
        return false;
    }
    m_observedDemoPath = path;
    return true;
}

void Cs2PluginClient::RegisterPendingSeekAck(uint64_t commandId, int targetTick, bool hasPauseAfterSeek,
                                             bool pauseAfterSeek)
{
    uint64_t supersededId = 0;
    {
        std::lock_guard<std::mutex> lock(m_pendingSeekAckMutex);
        if (m_pendingSeekAck.active)
        {
            supersededId = m_pendingSeekAck.commandId;
        }
        int preSeekTick = m_currentTick.load();
        m_pendingSeekAck.active = true;
        m_pendingSeekAck.commandId = commandId;
        m_pendingSeekAck.targetTick = targetTick;
        // Backward seeks (target at/below the pre-seek position) reload the demo: the
        // observed tick restarts near 0 and rises back to the target, so the arrival
        // check must not fire on the still-high pre-seek ticks. Await "descent" first
        // (see CheckPendingSeekAck). Forward seeks - and seeks issued before any tick
        // was observed - arm immediately.
        m_pendingSeekAck.awaitingDescent = (preSeekTick >= 0 && targetTick <= preSeekTick);
        m_pendingSeekAck.hasPauseAfterSeek = hasPauseAfterSeek;
        m_pendingSeekAck.pauseAfterSeek = pauseAfterSeek;
        m_pendingSeekAck.deadline = std::chrono::steady_clock::now() + kSeekArrivalTimeout;
    }
    Log("[gRPC] Pending seek ack registered: id=%llu target=%d", static_cast<unsigned long long>(commandId),
        targetTick);
    if (supersededId != 0)
    {
        SendCommandAck(supersededId, false, "Superseded by a newer SetDemoTick command");
    }
}

void Cs2PluginClient::CheckPendingSeekAck(bool isPlayingDemo, int observedTick)
{
    uint64_t completedId = 0;
    uint64_t timedOutId = 0;
    int targetTick = 0;
    bool hasPauseAfterSeek = false;
    bool pauseAfterSeek = false;
    {
        std::lock_guard<std::mutex> lock(m_pendingSeekAckMutex);
        if (!m_pendingSeekAck.active)
        {
            return;
        }
        targetTick = m_pendingSeekAck.targetTick;
        if (std::chrono::steady_clock::now() >= m_pendingSeekAck.deadline)
        {
            timedOutId = m_pendingSeekAck.commandId;
            m_pendingSeekAck.active = false;
        }
        else if (isPlayingDemo && observedTick >= 0)
        {
            // Two-phase arrival predicate (tolerance +-16, see header doc). Cycles
            // where the demo is not playing (a backward seek's reload passes through a
            // stopped state) or no tick is available are skipped, never failed.
            if (m_pendingSeekAck.awaitingDescent && observedTick <= targetTick + kSeekArrivalToleranceTicks)
            {
                m_pendingSeekAck.awaitingDescent = false; // Restart (real reload) or direct jump (mock) observed
            }
            if (!m_pendingSeekAck.awaitingDescent && observedTick >= targetTick - kSeekArrivalToleranceTicks)
            {
                completedId = m_pendingSeekAck.commandId;
                hasPauseAfterSeek = m_pendingSeekAck.hasPauseAfterSeek;
                pauseAfterSeek = m_pendingSeekAck.pauseAfterSeek;
                m_pendingSeekAck.active = false;
            }
        }
    }

    // A host seek was in transit this cycle (or just resolved): keep the suppression window
    // fresh. A backward seek's reload can outlast the 3 s window (the slot's own arrival
    // deadline is 30 s), and its stop/start transitions must never be attributed
    // ORIGIN_USER. Mirrors HandleLoadDemo's refresh-while-waiting loop.
    NotePlaybackCommand();

    if (timedOutId != 0)
    {
        std::string errorMessage = "Seek to tick " + std::to_string(targetTick) + " did not arrive within " +
                                   std::to_string(kSeekArrivalTimeout.count()) + " s";
        std::cerr << "CheckPendingSeekAck: " << errorMessage << std::endl;
        Log("[gRPC] %s", errorMessage.c_str());
        SendCommandAck(timedOutId, false, errorMessage);
        return;
    }
    if (completedId == 0)
    {
        return;
    }

    Log("[gRPC] Seek arrival: target=%d observed=%d", targetTick, observedTick);

    // Apply pause_after_seek only after arrival (acked seeks defer the state intent so the
    // engine's transit/reload cannot clobber it). This runs in snapshot context - on
    // the engine thread while the frame hook is alive - following the
    // PlayDemoTickRange auto-pause precedent.
    if (hasPauseAfterSeek)
    {
        const char *stateCmd = pauseAfterSeek ? "demo_pause" : "demo_resume";
        Log("[gRPC] %s (pause_after_seek, on seek arrival)", stateCmd);
        NotePlaybackCommand(); // The resulting engine pause flip is host-driven
        m_isPaused = pauseAfterSeek;
        ExecuteEngineCommand(stateCmd);
    }

    // Force tick update at key moment (seek arrival) - with the observed tick, not the
    // target (the target pre-echo is exactly what acked seeks eliminate).
    SendTickUpdateIfNeeded(observedTick, true);
    SendCommandAck(completedId, true, "", observedTick);
}

void Cs2PluginClient::UpdateTick(int tick)
{
    int previousTick = m_currentTick.exchange(tick);

    ProcessScheduledActions(previousTick, tick);

    if (previousTick != tick)
    {
        SendTickUpdateIfNeeded(tick, false);
    }
}

void Cs2PluginClient::SendTickUpdateIfNeeded(int tick, bool force)
{
    int frequency = m_tickUpdateFrequency.load();
    int lastSent = m_lastSentTick.load();

    // Always send if:
    // - force = true (key moment like pause, resume, seek)
    // - frequency = 0 (send every tick - original behavior)
    // - this is the first tick update (lastSent < 0)
    // - tick went backwards (seek or new demo loaded)
    // - tick is at or past next scheduled update
    bool shouldSend = force || frequency == 0 || lastSent < 0 || tick < lastSent || (tick - lastSent) >= frequency;

    if (shouldSend)
    {
        // Engine-truth pause once polled, echo fallback otherwise ("engine-pause-detection")
        SendTickUpdate(tick, EffectivePausedState());
        m_lastSentTick.store(tick);
    }
}

void Cs2PluginClient::ProcessScheduledActions(int lastObservedTick, int currentTick)
{
    // Half-open interval (lastObservedTick, currentTick] - mirv_cmd/CSDM semantics.
    // Ticks can be skipped between observations (fast playback, seeks), so every
    // entry inside the crossed interval fires; backward movement fires nothing (a
    // backward demo_gototick reloads the demo and must not fire in-between actions).
    if (currentTick <= lastObservedTick)
    {
        return;
    }

    std::vector<std::pair<int, ScheduledTickAction>> actionsToExecute;
    {
        std::lock_guard<std::mutex> lock(m_tickActionsMutex);
        auto it = m_tickActions.upper_bound(lastObservedTick);
        while (it != m_tickActions.end() && it->first <= currentTick)
        {
            actionsToExecute.emplace_back(it->first, std::move(it->second));
            it = m_tickActions.erase(it);
        }
    }

    // Execute actions outside the lock to avoid deadlocks. Commands go through
    // ExecuteEngineCommand like every other call site; when this runs in the frame
    // hook, OnEngineFrame's post-snapshot drain executes them in the same frame, so
    // scheduled batches stay tick-exact.
    for (auto &[scheduledTick, action] : actionsToExecute)
    {
        Log("[gRPC] Executing scheduled action %d at tick %d (scheduled for %d)", action.id, currentTick,
            scheduledTick);
        if (action.callback)
        {
            action.callback(currentTick);
        }
        for (const std::string &cmd : action.commands)
        {
            Log("[CS2 - Scheduled @%d] %s", scheduledTick, cmd.c_str());
            ExecuteEngineCommand(cmd);
        }
    }
}

int Cs2PluginClient::ScheduleAtTick(int tick, TickActionCallback callback)
{
    int actionId = m_nextActionId.fetch_add(1);
    ScheduledTickAction action;
    action.id = actionId;
    action.callback = std::move(callback);
    {
        std::lock_guard<std::mutex> lock(m_tickActionsMutex);
        m_tickActions.emplace(tick, std::move(action));
    }
    Log("[gRPC] Scheduled action %d at tick %d", actionId, tick);
    return actionId;
}

int Cs2PluginClient::ScheduleCommandsAtTick(int tick, std::vector<std::string> commands)
{
    int actionId = m_nextActionId.fetch_add(1);
    ScheduledTickAction action;
    action.id = actionId;
    action.hostOwned = true;
    action.commands = std::move(commands);
    size_t commandCount = action.commands.size();
    {
        std::lock_guard<std::mutex> lock(m_tickActionsMutex);
        m_tickActions.emplace(tick, std::move(action));
    }
    Log("[gRPC] Scheduled %zu host command(s) (action %d) at tick %d", commandCount, actionId, tick);
    return actionId;
}

void Cs2PluginClient::ClearScheduledActions(ScheduledActionScope scope)
{
    std::lock_guard<std::mutex> lock(m_tickActionsMutex);
    size_t removed = 0;
    for (auto it = m_tickActions.begin(); it != m_tickActions.end();)
    {
        bool matches =
            scope == ScheduledActionScope::All || (scope == ScheduledActionScope::HostOnly) == it->second.hostOwned;
        if (matches)
        {
            it = m_tickActions.erase(it);
            ++removed;
        }
        else
        {
            ++it;
        }
    }
    if (removed > 0)
    {
        Log("[gRPC] Cleared %zu scheduled action(s) (scope %d)", removed, static_cast<int>(scope));
    }
}

void Cs2PluginClient::HandleControlRequest(const cs2_video_generator::CsControlRequest &request)
{
    std::cout << "Received control request from server" << std::endl;

    // Envelope-level correlation id ("command-ack" capability): non-zero means the
    // orchestrator wants a CommandAckEvent once the command has been applied. The ack
    // is additive - handlers that already echo status keep doing so.
    uint64_t commandId = request.has_command_id() ? request.command_id() : 0;

    CommandOutcome outcome;
    switch (request.command_case())
    {
    case cs2_video_generator::CsControlRequest::kLoadDemo:
        outcome = HandleLoadDemo(request.load_demo());
        break;
    case cs2_video_generator::CsControlRequest::kResumeDemo:
        outcome = HandleResumeDemo(request.resume_demo());
        break;
    case cs2_video_generator::CsControlRequest::kPauseDemo:
        outcome = HandlePauseDemo(request.pause_demo());
        break;
    case cs2_video_generator::CsControlRequest::kCloseDemo:
        outcome = HandleCloseDemo(request.close_demo());
        break;
    case cs2_video_generator::CsControlRequest::kSetDemoTick:
        outcome = HandleSetDemoTick(request.set_demo_tick(), commandId);
        break;
    case cs2_video_generator::CsControlRequest::kPlayDemoTickRange:
        outcome = HandlePlayDemoTickRange(request.play_demo_tick_range());
        break;
    case cs2_video_generator::CsControlRequest::kSetSpectatorTarget:
        outcome = HandleSetSpectatorTarget(request.set_spectator_target());
        break;
    case cs2_video_generator::CsControlRequest::kSetDemoOptions:
        outcome = HandleSetDemoOptions(request.set_demo_options());
        break;
    case cs2_video_generator::CsControlRequest::kSetTickUpdateFrequency:
        outcome = HandleSetTickUpdateFrequency(request.set_tick_update_frequency());
        break;
    case cs2_video_generator::CsControlRequest::kSetDemoTimescale:
        outcome = HandleSetDemoTimescale(request.set_demo_timescale());
        break;
    case cs2_video_generator::CsControlRequest::kQueryDemoState:
        outcome = HandleQueryDemoState(request.query_demo_state(), commandId);
        break;
    case cs2_video_generator::CsControlRequest::kScheduleCommandsAtTick:
        outcome = HandleScheduleCommandsAtTick(request.schedule_commands_at_tick());
        break;
    case cs2_video_generator::CsControlRequest::kClearScheduledCommands:
        outcome = HandleClearScheduledCommands(request.clear_scheduled_commands());
        break;
    case cs2_video_generator::CsControlRequest::kSetGameEventSubscription:
        outcome = HandleSetGameEventSubscription(request.set_game_event_subscription());
        break;
    case cs2_video_generator::CsControlRequest::kExecuteConsoleCommands:
        outcome = HandleExecuteConsoleCommands(request.execute_console_commands());
        break;
    case cs2_video_generator::CsControlRequest::COMMAND_NOT_SET:
    default:
        std::cerr << "Received unknown or empty control request" << std::endl;
        outcome = {false, "Unknown or empty command"};
        break;
    }

    if (commandId != 0 && !outcome.ackDeferred)
    {
        SendCommandAck(commandId, outcome.success, outcome.errorMessage);
    }
}

namespace
{
/**
 * @brief Lowercased final path component, for case-insensitive demo-identity compares.
 *
 * Manual split rather than std::filesystem: the engine may hand back either separator
 * style (and a relativized or normalized form) regardless of host platform, and the
 * result is only ever compared against another basename, never used as a path.
 */
std::string LowercaseBasename(const std::string &path)
{
    size_t sep = path.find_last_of("/\\");
    std::string name = (sep == std::string::npos) ? path : path.substr(sep + 1);
    for (char &c : name)
    {
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return name;
}
} // namespace

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleLoadDemo(const cs2_video_generator::LoadDemoCommand &cmd)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandleLoadDemo: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }

    std::cout << "Orchestrating LoadDemo for: " << cmd.demo_path() << std::endl;

    // 1. Validate the demo file exists before touching the engine ("load-failure-detection").
    //    playdemo silently no-ops on a missing file; previously that still ended in a
    //    DEMO_LOADED echo after the blind wait. u8path: proto strings are UTF-8.
    std::error_code existsError;
    if (!std::filesystem::exists(std::filesystem::u8path(cmd.demo_path()), existsError) || existsError)
    {
        std::string errorMessage = "Demo file not found: " + cmd.demo_path();
        std::cerr << "HandleLoadDemo: " << errorMessage << std::endl;
        Log("[gRPC] %s", errorMessage.c_str());
        SendPlaybackStatusChange(cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus_DEMO_FILE_NOT_FOUND,
                                 errorMessage);
        return {false, errorMessage};
    }

    // 2. Send DEMO_LOADING status
    SendPlaybackStatusChange(cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus_DEMO_LOADING);

    // A new demo invalidates the tick space: drop every unfired scheduled action,
    // internal and host-scheduled alike (documented in ScheduleCommandsAtTickCommand).
    ClearScheduledActions(ScheduledActionScope::All);

    // 3. Call playdemo. The pre-playdemo identity snapshot feeds the load-completion
    //    check below ("demo-identity"): IsPlayingDemo alone reads stale-true during a
    //    direct re-load while another demo is still playing, so completion must also
    //    see the observed path leave its pre-playdemo value.
    std::string prePlaydemoPath = ObservedDemoPath();
    std::string requestedBasename = LowercaseBasename(cmd.demo_path());
    NotePlaybackCommand();
    std::string playCmd = "playdemo \"" + cmd.demo_path() + "\"";
    Log("[gRPC] playdemo via gRPC: %s", playCmd.c_str());
    ExecuteEngineCommand(playCmd);

    // 4. Wait for the demo to actually start instead of the previous blind 15 s sleep.
    //    Polls the playing state observed by the polling thread (engine reads stay
    //    confined to the guarded polling path). Real CS2 loads take ~10-15 s; the mock
    //    engine flips IsPlayingDemo immediately, so mock loads become near-instant.
    //    The suppression-window stamp is refreshed while we wait so the polling thread
    //    attributes the observed demo start to this host command, however long the
    //    engine takes to get there.
    //
    //    Completion condition ("demo-identity" upgrade): IsPlayingDemo and the observed
    //    GetDemoFilePath corresponds to the requested demo - basenames compared
    //    case-insensitively, because the engine may return a relativized or normalized
    //    form of the playdemo argument. When the basenames do not match but the path
    //    changed from its pre-playdemo snapshot value, that change right after our
    //    playdemo is still taken as our load (unknowable engine path mapping). When the
    //    engine exposes no path at all (null/empty, e.g. older builds), fall back to
    //    the plain IsPlayingDemo poll.
    //    The poll interval is short (the observed state is refreshed once per engine
    //    frame now, not once per 100 ms) so a fast mock load is not rounded up; the
    //    60 s timeout stays as the failure-detection backstop.
    constexpr int loadPollIntervalMs = 5;
    constexpr int loadTimeoutMs = 60000;
    bool demoStarted = false;
    for (int elapsedMs = 0; elapsedMs < loadTimeoutMs; elapsedMs += loadPollIntervalMs)
    {
        NotePlaybackCommand();
        if (m_wasPlayingDemo.load())
        {
            std::string observedPath = ObservedDemoPath();
            // A transiently empty path during a direct re-load is the old demo's teardown
            // clearing the engine path while IsPlayingDemo still reads stale-true; it must
            // not satisfy the wait. The empty-path fallback applies only when the engine
            // never exposed a path at all (older builds: pre-playdemo snapshot empty too),
            // and the changed-path condition requires an actual non-empty new path.
            if ((observedPath.empty() && prePlaydemoPath.empty())       // Engine exposes no path: plain playing poll
                || LowercaseBasename(observedPath) == requestedBasename // Identity-confirmed: our demo is up
                || (!observedPath.empty() && observedPath != prePlaydemoPath)) // Non-empty NEW path after our playdemo
            {
                demoStarted = true;
                break;
            }
            // else: stale-true, the previous demo is still playing (or tearing down); keep waiting.
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(loadPollIntervalMs));
    }

    if (!demoStarted)
    {
        std::string errorMessage =
            "Demo did not start playing within " + std::to_string(loadTimeoutMs / 1000) + " s: " + cmd.demo_path();
        std::cerr << "HandleLoadDemo: " << errorMessage << std::endl;
        Log("[gRPC] %s", errorMessage.c_str());
        SendPlaybackStatusChange(cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus_DEMO_FILE_UNPLAYABLE,
                                 errorMessage);
        return {false, errorMessage};
    }

    // 5. Pause the demo
    Log("[gRPC] pause demo");
    NotePlaybackCommand();
    m_isPaused = true;
    ExecuteEngineCommand("demo_pause");

    // 6. Seek to tick 0
    char buf[64];
    std::snprintf(buf, sizeof(buf), "demo_gototick %d", 0);
    Log("[gRPC] goto tick: %d", 0);
    ExecuteEngineCommand(buf);

    // 7. Wait for the observed tick to settle at 0. There is still no seek-complete
    //    signal from the engine, so this stays a bounded poll (2 s cap, silent
    //    fall-through) - only the granularity improves: the observed tick is now
    //    refreshed once per engine frame, so arrival is seen within a frame instead of
    //    up to 20 ms late.
    for (int elapsedMs = 0; elapsedMs < 2000; elapsedMs += 5)
    {
        if (m_currentTick.load() == 0)
        {
            break;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }

    // 8. One-time setup console commands. Per-clip options (xray, true view, assists,
    //    death notices, spectator target) arrive later via SetDemoOptions and
    //    SetSpectatorTarget.
    std::vector<std::string> cmds = {
        "sv_cheats 1",                             // Enable cheats for demo playback. Allows for other commands to work
        "volume 1",                                // Ensure volume is on
        "cl_hud_telemetry_frametime_show 0",       // Disable telemetry overlays
        "cl_hud_telemetry_net_misdelivery_show 0", // Disable telemetry overlays
        "cl_hud_telemetry_ping_show 0",            // Disable telemetry overlays
        "cl_hud_telemetry_serverrecvmargin_graph_show 0", // Disable telemetry overlays
        "cl_trueview_show_status 0",                      // Hide True View status
        "r_show_build_info 0",                            // Hide build info overlay
        "spec_mode 1",                                    // First person spectator mode
    };

    if (cmd.interactive_demo_ui())
    {
        // "user-demo-ui" capability: the startup path disables the demo UI once at plugin
        // connect (demo_ui_mode 0, before any command arrives); interactive mode must
        // actively re-enable it here, and skips the per-load "demoui 0".
        cmds.push_back("demo_ui_mode 1"); // Re-enable the demo playback UI for the user
    }
    else
    {
        cmds.push_back("demoui 0"); // Hide demo playback UI
    }

    // The whole batch is queued for the engine thread and drained in a single
    // FRAME_START (CSDM executes multi-command batches per frame the same way). The
    // former 50 ms inter-command sleeps existed only because the commands used to be
    // issued from a background thread with no processing guarantee.
    std::cout << "Queueing " << cmds.size() << " console commands..." << std::endl;
    for (const auto &consoleCmd : cmds)
    {
        std::cout << "[Console] " << consoleCmd << std::endl;
        Log("[CS2 - Console Execute] %s", consoleCmd.c_str());
        ExecuteEngineCommand(consoleCmd);
    }
    std::cout << "Console commands queued" << std::endl;

    // 9. Send DEMO_LOADED status
    SendPlaybackStatusChange(cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus_DEMO_LOADED);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleResumeDemo(
    const cs2_video_generator::ResumeDemoCommand & /* cmd */)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandleResumeDemo: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }
    std::cout << "Executing ResumeDemo command" << std::endl;
    Log("[gRPC] resume demo");
    NotePlaybackCommand();
    m_isPaused = false;
    ExecuteEngineCommand("demo_resume");

    SendTickUpdateIfNeeded(m_currentTick.load(), true);

    using DemoStatus = cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus;
    SendPlaybackStatusChange(DemoStatus::DemoPlaybackStatusChange_DemoPlaybackStatus_PLAYING);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandlePauseDemo(
    const cs2_video_generator::PauseDemoCommand & /* cmd */)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandlePauseDemo: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }
    std::cout << "Executing PauseDemo command" << std::endl;
    Log("[gRPC] pause demo");
    NotePlaybackCommand();
    m_isPaused = true;
    ExecuteEngineCommand("demo_pause");

    SendTickUpdateIfNeeded(m_currentTick.load(), true);

    using DemoStatus = cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus;
    SendPlaybackStatusChange(DemoStatus::DemoPlaybackStatusChange_DemoPlaybackStatus_PAUSED);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleCloseDemo(
    const cs2_video_generator::CloseDemoCommand & /* cmd */)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandleCloseDemo: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }
    std::cout << "Executing CloseDemo command" << std::endl;
    Log("[gRPC] stop demo");
    NotePlaybackCommand();
    ExecuteEngineCommand("stop");

    // SendPlaybackStatusChange uses the tracked status as "previous" automatically.
    using DemoStatus = cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus;
    SendPlaybackStatusChange(DemoStatus::DemoPlaybackStatusChange_DemoPlaybackStatus_STOPPED);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleSetDemoTick(const cs2_video_generator::SetDemoTickCommand &cmd,
                                                                   uint64_t commandId)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandleSetDemoTick: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }
    std::cout << "Executing SetDemoTick command to tick: " << cmd.demo_tick() << std::endl;

    // Arrival-verified seek ("seek-ack"): a command_id means the ack is deferred until
    // the polling thread observes the engine at the target (or the 30 s timeout).
    // Registered before the demo_gototick so the backward/forward classification sees
    // the pre-seek tick; supersedes (fails) any still-pending earlier seek ack. The
    // reader thread does not wait - completion happens on the polling thread.
    bool arrivalVerified = commandId != 0;
    if (arrivalVerified)
    {
        RegisterPendingSeekAck(commandId, cmd.demo_tick(), cmd.has_pause_after_seek(), cmd.pause_after_seek());
    }

    // Suppression stamp: a backwards demo_gototick reloads the demo, which the polling
    // thread can observe as a stop/start transition - attribute it to this command.
    NotePlaybackCommand();
    char buf[64];
    std::snprintf(buf, sizeof(buf), "demo_gototick %d", cmd.demo_tick());
    Log("[gRPC] goto tick: %d", cmd.demo_tick());
    ExecuteEngineCommand(buf);

    if (arrivalVerified)
    {
        // No target-tick pre-echo and no immediate pause/resume: the forced tick update
        // (with the observed tick) and pause_after_seek both happen at arrival, from
        // CheckPendingSeekAck on the polling thread.
        return {true, "", /*ackDeferred*/ true};
    }

    // Bare/legacy seek (no command_id): honor pause_after_seek as immediate state
    // intent and pre-echo the target tick, matching pre-"seek-ack" behavior.
    if (cmd.has_pause_after_seek())
    {
        const char *stateCmd = cmd.pause_after_seek() ? "demo_pause" : "demo_resume";
        Log("[gRPC] %s (pause_after_seek)", stateCmd);
        m_isPaused = cmd.pause_after_seek();
        ExecuteEngineCommand(stateCmd);
    }

    SendTickUpdateIfNeeded(cmd.demo_tick(), true);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandlePlayDemoTickRange(
    const cs2_video_generator::PlayDemoTickRangeCommand &cmd)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandlePlayDemoTickRange: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }

    std::cout << "Executing PlayDemoTickRange command: " << cmd.starting_tick() << " to " << cmd.ending_tick()
              << std::endl;

    int startTick = cmd.starting_tick();
    int endTick = cmd.ending_tick();

    // Clear any previously scheduled internal actions to avoid stale pauses
    // (host-scheduled command batches are deliberately preserved)
    ClearScheduledActions(ScheduledActionScope::InternalOnly);

    // Schedule pause at end tick (use engine getter in lambda for thread safety)
    ScheduleAtTick(endTick, [this](int actualTick) {
        Log("[gRPC] Auto-pausing at end of range (tick %d)", actualTick);

        // The resulting engine pause flip is host-driven (this range command scheduled it).
        // Stamp the suppression window here, in snapshot context: the command-time stamp in
        // HandlePlayDemoTickRange cannot cover a range that outlasts the ~3 s window (i.e.
        // essentially every real clip), and without it the snapshot processing emits the
        // auto-pause as an ORIGIN_USER DemoStateEvent. Same precedent as
        // CheckPendingSeekAck's pause_after_seek.
        NotePlaybackCommand();

        Log("[gRPC] pause demo");
        m_isPaused = true;
        ExecuteEngineCommand("demo_pause");

        SendTickUpdateIfNeeded(actualTick, true);

        using DemoStatus = cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus;
        SendPlaybackStatusChange(DemoStatus::DemoPlaybackStatusChange_DemoPlaybackStatus_PAUSED);
    });

    char buf[128];
    std::snprintf(buf, sizeof(buf), "demo_gototick %d", startTick);
    Log("[gRPC] play range: %d-%d", startTick, endTick);
    NotePlaybackCommand();
    ExecuteEngineCommand(buf);

    Log("[gRPC] resume demo");
    m_isPaused = false;
    ExecuteEngineCommand("demo_resume");

    using DemoStatus = cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus;
    SendPlaybackStatusChange(DemoStatus::DemoPlaybackStatusChange_DemoPlaybackStatus_PLAYING);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleSetSpectatorTarget(
    const cs2_video_generator::SetSpectatorTargetCommand &cmd)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandleSetSpectatorTarget: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }

    const std::string &playerName = cmd.player_name();
    std::cout << "Executing SetSpectatorTarget command for player: " << playerName << std::endl;

    // Use spec_player with player name (quoted to handle spaces)
    std::string specCmd = "spec_player \"" + playerName + "\"";
    Log("[gRPC] %s", specCmd.c_str());
    ExecuteEngineCommand(specCmd);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleSetDemoOptions(
    const cs2_video_generator::SetDemoOptionsCommand &cmd)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandleSetDemoOptions: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }

    std::cout << "Executing SetDemoOptions command" << std::endl;

    const char *xrayCmd = cmd.xray_enabled() ? "spec_show_xray 1" : "spec_show_xray 0";
    Log("[gRPC] %s", xrayCmd);
    ExecuteEngineCommand(xrayCmd);

    // True View (demo prediction for first-person view)
    const char *trueViewCmd = cmd.true_view_enabled() ? "cl_demo_predict 1" : "cl_demo_predict 0";
    Log("[gRPC] %s", trueViewCmd);
    ExecuteEngineCommand(trueViewCmd);

    const char *hudCmd = cmd.hud_enabled() ? "cl_drawhud 1" : "cl_drawhud 0";
    Log("[gRPC] %s", hudCmd);
    ExecuteEngineCommand(hudCmd);

    const char *assistsCmd = cmd.show_assists_in_feed() ? "mp_display_kill_assists 1" : "mp_display_kill_assists 0";
    Log("[gRPC] %s", assistsCmd);
    ExecuteEngineCommand(assistsCmd);

    // Show only death notices (hide everything else)
    const char *deathNoticesCmd =
        cmd.show_only_death_notices() ? "cl_draw_only_deathnotices 1" : "cl_draw_only_deathnotices 0";
    Log("[gRPC] %s", deathNoticesCmd);
    ExecuteEngineCommand(deathNoticesCmd);

    std::cout << "Demo options applied - xray:" << cmd.xray_enabled() << " trueView:" << cmd.true_view_enabled()
              << " hud:" << cmd.hud_enabled() << " assists:" << cmd.show_assists_in_feed()
              << " deathNoticesOnly:" << cmd.show_only_death_notices() << std::endl;
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleSetTickUpdateFrequency(
    const cs2_video_generator::SetTickUpdateFrequencyCommand &cmd)
{
    int newFrequency = cmd.frequency();
    if (newFrequency < 0)
        newFrequency = 0; // Clamp negative values to 0 (every tick)

    int oldFrequency = m_tickUpdateFrequency.exchange(newFrequency);
    std::cout << "Tick update frequency changed: " << oldFrequency << " -> " << newFrequency << std::endl;
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleSetDemoTimescale(
    const cs2_video_generator::SetDemoTimescaleCommand &cmd)
{
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "HandleSetDemoTimescale: Engine not available" << std::endl;
        return {false, "Engine not available"};
    }
    std::cout << "Executing SetDemoTimescale command: " << cmd.timescale() << std::endl;
    char buf[64];
    std::snprintf(buf, sizeof(buf), "demo_timescale %.3f", cmd.timescale());
    Log("[gRPC] %s", buf);
    ExecuteEngineCommand(buf);

    // Remember the last host-set value for DemoStateEvent.demo_timescale ("timescale-set"
    // capability; no engine readback in this version).
    m_demoTimescale.store(cmd.timescale());
    m_timescaleSet.store(true);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleQueryDemoState(
    const cs2_video_generator::QueryDemoStateCommand & /* cmd */, uint64_t commandId)
{
    std::cout << "Executing QueryDemoState command" << std::endl;
    // Snapshot from the polling thread's tracked view (fresh within one ~120 Hz poll
    // cycle) rather than a direct engine read - engine reads stay confined to the
    // guarded polling path. The reply is the completion payload of a host command, so
    // origin is ORIGIN_HOST_COMMAND; correlation happens via in_reply_to_command_id.
    SendDemoStateEvent(m_wasPlayingDemo.load(), m_currentTick.load(),
                       cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_HOST_COMMAND, commandId);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleScheduleCommandsAtTick(
    const cs2_video_generator::ScheduleCommandsAtTickCommand &cmd)
{
    if (cmd.demo_tick() < 0)
    {
        return {false, "demo_tick must be non-negative"};
    }
    if (cmd.console_commands_size() == 0)
    {
        return {false, "console_commands must not be empty"};
    }

    std::vector<std::string> commands(cmd.console_commands().begin(), cmd.console_commands().end());
    int actionId = ScheduleCommandsAtTick(cmd.demo_tick(), std::move(commands));
    std::cout << "Scheduled " << cmd.console_commands_size() << " command(s) at tick " << cmd.demo_tick() << " (action "
              << actionId << ")" << std::endl;
    // Success = registration (see the proto doc): execution happens later, on the
    // engine thread, when the observed tick crosses the target.
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleClearScheduledCommands(
    const cs2_video_generator::ClearScheduledCommandsCommand & /* cmd */)
{
    std::cout << "Executing ClearScheduledCommands command" << std::endl;
    ClearScheduledActions(ScheduledActionScope::HostOnly);
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleExecuteConsoleCommands(
    const cs2_video_generator::ExecuteConsoleCommandsCommand &cmd)
{
    if (cmd.console_commands_size() == 0)
    {
        return {false, "console_commands must not be empty"};
    }

    std::cout << "Executing " << cmd.console_commands_size() << " console command(s)" << std::endl;
    for (const std::string &command : cmd.console_commands())
    {
        Log("[CS2 - Console Exec] %s", command.c_str());
        ExecuteEngineCommand(command);
    }

    // Success = queued (see the proto doc): the commands run on the engine thread at the
    // next frame drain, and the engine reports nothing back either way. The suppression
    // window is deliberately not stamped - a blind console command is not a demo-control
    // command, so any state change it causes is attributed like a fired tick schedule.
    return {};
}

Cs2PluginClient::CommandOutcome Cs2PluginClient::HandleSetGameEventSubscription(
    const cs2_video_generator::SetGameEventSubscriptionCommand &cmd)
{
    if (cmd.enabled())
    {
        // Lazy hook install (hard requirement: never at plugin load). Idempotent per
        // binary; failure fails the command so an acked send surfaces it to the host.
        if (!CsvgEnsureGameEventHookInstalled())
        {
            std::string errorMessage = "Game-event telemetry hook could not be installed on this platform/build";
            std::cerr << "HandleSetGameEventSubscription: " << errorMessage << std::endl;
            Log("[GameEvents] %s", errorMessage.c_str());
            return {false, errorMessage};
        }

        {
            std::lock_guard<std::mutex> lock(m_gameEventFilterMutex);
            m_gameEventFilter.assign(cmd.event_names().begin(), cmd.event_names().end());
        }
        EnsureGameEventSenderStarted();
        m_gameEventsEnabled.store(true);
        Log("[GameEvents] Telemetry ENABLED (%d event name filter(s); empty filter = all events)",
            cmd.event_names_size());
    }
    else
    {
        // Disable is the master switch; the (pass-through) hook stays installed.
        m_gameEventsEnabled.store(false);
        std::lock_guard<std::mutex> lock(m_gameEventFilterMutex);
        m_gameEventFilter.clear();
        Log("[GameEvents] Telemetry disabled");
    }
    return {};
}

bool Cs2PluginClient::WaitForChannelReady(std::chrono::seconds timeout)
{
    auto deadline = std::chrono::system_clock::now() + timeout;
    return channel_->WaitForConnected(deadline);
}

namespace
{
// Thread-safety guard for the engine reads (DemoStateSnapshot is declared in the
// header - the engine-thread frame hook fills the same struct).
//
// This polling thread reads demo state (IsPlayingDemo / GetDemoFile / GetDemoTick)
// off CS2's main thread. ISource2EngineToClient is not thread-safe: the main thread
// can tear down the demo player (demo end / stop / reload) while we read it, so a
// read may dereference freed memory and raise an ACCESS_VIOLATION inside engine2.dll.
// These three calls are pure, side-effect-free getters, so on Windows we run them
// inside a structured-exception guard and treat a fault as "no snapshot this cycle"
// instead of letting it crash CS2. Skipping a raced read cannot corrupt engine state,
// and the poll self-corrects on the next cycle. The caller logs when a fault is
// caught (see EnginePollingLoop) so a race is visible in csvg.log rather than a
// silent stall or a hard crash.
//
// __try requires a function with no C++ objects needing unwinding, so the guarded
// reads live in this dedicated helper with only POD locals.
//
// Linux has no SEH, so the reads run unguarded there. The plugin is Windows-first
// and every observed crash is Windows; a Linux teardown race would need a
// signal-handler approach instead, which is out of scope until it's actually seen.
#ifdef _WIN32
static bool ReadDemoStateSnapshot(ISource2EngineToClient *engine, DemoStateSnapshot *out)
{
    __try
    {
        out->isPlayingDemo = engine->IsPlayingDemo();
        out->currentTick = -1;
        out->isPaused = false;
        out->pausedValid = false;
        if (out->isPlayingDemo)
        {
            IDemoFile *demoFile = engine->GetDemoFile();
            if (demoFile != nullptr)
            {
                out->currentTick = demoFile->GetDemoTick();
                // Engine-truth pause ("engine-pause-detection"): IDemoFile::IsDemoPaused,
                // win slot 012. Same read-only teardown-race profile as GetDemoTick.
                out->isPaused = demoFile->IsDemoPaused();
                out->pausedValid = true;
            }
        }
        // Demo identity ("demo-identity"): GetDemoFilePath (slot 043) returns a pointer
        // into engine memory that the main thread can free during demo teardown - copy
        // IMMEDIATELY, inside the guard, with a bounded POD loop (__try forbids CRT/C++
        // constructs needing unwinding). Read even when not playing so a cleared path
        // is observed as a change.
        const char *path = engine->GetDemoFilePath();
        size_t i = 0;
        if (path != nullptr)
        {
            for (; i < sizeof(out->demoFilePath) - 1 && path[i] != '\0'; ++i)
            {
                out->demoFilePath[i] = path[i];
            }
        }
        out->demoFilePath[i] = '\0';
        return true;
    }
    __except (1 /* EXCEPTION_EXECUTE_HANDLER */)
    {
        return false;
    }
}
#else
static bool ReadDemoStateSnapshot(ISource2EngineToClient *engine, DemoStateSnapshot *out)
{
    out->isPlayingDemo = engine->IsPlayingDemo();
    out->currentTick = -1;
    out->isPaused = false;
    out->pausedValid = false;
    if (out->isPlayingDemo)
    {
        IDemoFile *demoFile = engine->GetDemoFile();
        if (demoFile != nullptr)
        {
            out->currentTick = demoFile->GetDemoTick();
            // Engine-truth pause: IsDemoPaused is declared for this platform too
            // (linux slot 013 - the Itanium dtor shift is baked into the class layout).
            out->isPaused = demoFile->IsDemoPaused();
            out->pausedValid = true;
        }
    }
    // Demo identity: null check is the only defense available here - no SEH on this
    // platform, so a copy racing demo teardown carries the same (accepted) residual
    // risk as the unguarded reads above. See the guard comment at the top of this
    // namespace; a signal-handler guard stays out of scope until a crash is actually
    // observed off-Windows.
    const char *path = engine->GetDemoFilePath();
    size_t i = 0;
    if (path != nullptr)
    {
        for (; i < sizeof(out->demoFilePath) - 1 && path[i] != '\0'; ++i)
        {
            out->demoFilePath[i] = path[i];
        }
    }
    out->demoFilePath[i] = '\0';
    return true;
}
#endif
} // namespace

void Cs2PluginClient::EnginePollingLoop()
{
    std::cout << "[POLLING] Engine polling thread started" << std::endl;

    // Counts guarded engine-read faults so a rare demo-teardown race (an occasional
    // logged line) is distinguishable from a systemic problem such as stale engine
    // vtable offsets after a CS2 update (a large, fast-growing count).
    long readFaultCount = 0;
    auto lastFaultLog = std::chrono::steady_clock::now();

    while (!m_shouldStopPolling.load())
    {
        // Drive the engine-thread frame hook install from here. On the real plugin
        // client.dll is not loaded when the plugin's Connect runs, and the only other
        // install site (ClientFullyConnect) cannot fire until a demo is already being
        // brought up - which requires executing `playdemo`, the exact command that is
        // unsafe without the hook. So the loop retries on availability instead: one
        // atomic load per cycle once installed, a factory call per cycle before that.
        if (!CsvgEngineFrameHookInstalled())
        {
            CsvgTryInstallEngineFrameHook();
        }

        // Commands queued for an engine thread that has not appeared yet are reported
        // by name once they pass the deadline - a stalled hook must not look like a
        // silent hang.
        CheckStrandedEngineCommands();

        // Get engine dynamically - it may not be available immediately at startup
        auto engine = m_engineGetter ? m_engineGetter() : nullptr;
        if (engine == nullptr)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
            continue;
        }

        // Once the frame hook is installed it owns snapshot duty - reads on the engine
        // thread cannot race demo teardown at all - and this thread idles for the rest
        // of the session. Duty is deliberately not handed back when frames pause (a
        // demo load can take 10-15 s): see the header comment on the command queue.
        if (CsvgEngineFrameHookInstalled())
        {
            std::this_thread::sleep_for(std::chrono::microseconds(8333));
            continue;
        }

        // Poll engine state. Guarded because the engine may be tearing down the demo
        // player on the main thread concurrently; see ReadDemoStateSnapshot.
        DemoStateSnapshot snapshot;
        if (!ReadDemoStateSnapshot(engine, &snapshot))
        {
            // A read raced demo teardown on the main thread (or the engine vtable
            // offsets are stale for this CS2 build). Log it (always the first, then
            // rate-limited) so the fault is visible in csvg.log rather than a silent
            // stall, then skip this cycle and retry.
            ++readFaultCount;
            auto now = std::chrono::steady_clock::now();
            if (readFaultCount == 1 || now - lastFaultLog >= std::chrono::seconds(1))
            {
                lastFaultLog = now;
                Log("[POLLING] Engine demo-state read faulted (access violation), count=%ld. Likely a "
                    "demo-teardown race on CS2's main thread; skipping this poll cycle. A large, "
                    "fast-growing count instead points to stale engine vtable offsets in "
                    "cdll_interfaces.h for the current CS2 build.",
                    readFaultCount);
            }
            // A pending seek ack's 30 s deadline must keep ticking through a fault
            // storm (no arrival data this cycle - timeout evaluation only).
            CheckPendingSeekAck(false, -1);
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            continue;
        }

        m_firstSnapshotOk.store(true);
        ProcessEngineSnapshot(snapshot);

        // Poll at ~120Hz (faster than 64Hz tick rate to catch all changes)
        std::this_thread::sleep_for(std::chrono::microseconds(8333)); // ~120Hz
    }

    std::cout << "[POLLING] Engine polling thread stopped" << std::endl;
}

void Cs2PluginClient::ProcessEngineSnapshot(const DemoStateSnapshot &snapshot)
{
    // Serialized between the frame hook (engine thread) and the polling thread: at a
    // liveness boundary both can hold a snapshot at once, and the edge detection below
    // assumes sequential observations.
    std::lock_guard<std::mutex> processLock(m_snapshotProcessMutex);

    bool isPlayingDemo = snapshot.isPlayingDemo;
    int currentTick = snapshot.currentTick;

    // Demo identity ("demo-identity"): refresh the observed GetDemoFilePath copy
    // first, so everything below this line - the DemoStateEvents emitted this
    // cycle and HandleLoadDemo's load-completion poll (which reads it right after
    // seeing m_wasPlayingDemo flip) - sees this cycle's path.
    bool pathChanged = UpdateObservedDemoPath(snapshot.demoFilePath);

    // Engine-truth pause ("engine-pause-detection"): update the cache before any
    // event goes out so tick updates / state events below carry this cycle's truth.
    // The first valid read for a demo seeds the cache silently (a value appearing
    // is not a flip); a transiently invalid read (demo file briefly null) keeps the
    // last truth rather than bouncing the wire back to echo state.
    bool pauseFlipped = false;
    if (snapshot.pausedValid)
    {
        if (!m_enginePausedValid.load())
        {
            m_enginePaused.store(snapshot.isPaused);
            m_enginePausedValid.store(true);
        }
        else if (m_enginePaused.load() != snapshot.isPaused)
        {
            m_enginePaused.store(snapshot.isPaused);
            pauseFlipped = true;
        }
    }

    // Detect demo start - reset tick tracking and report the observed transition.
    // NOTE: Don't auto-send DemoPlaybackStatusChange - that channel stays a command
    // echo owned by the gRPC command handlers. Engine truth goes out as a
    // DemoStateEvent instead ("demo-state-events" capability), attributed via the
    // host-command suppression window.
    bool stateEventSent = false;
    bool wasPlaying = m_wasPlayingDemo.load();
    if (!wasPlaying && isPlayingDemo)
    {
        std::cout << "[STATE] Detected demo start" << std::endl;
        m_currentTick = -1;
        m_lastSentTick = -1; // Reset so first tick update is sent
        m_wasPlayingDemo = true;

        SendDemoStateEvent(true, currentTick,
                           IsWithinHostCommandWindow()
                               ? cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_HOST_COMMAND
                               : cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_USER);
        stateEventSent = true;
    }

    // Detect demo stop (demo end / user stop) - reset tick tracking and report the
    // observed transition (same DemoStateEvent channel and attribution as above).
    if (wasPlaying && !isPlayingDemo)
    {
        std::cout << "[STATE] Detected demo stop" << std::endl;
        int lastKnownTick = m_lastTick.load(); // Last observed tick before the stop
        m_currentTick = -1;
        m_wasPlayingDemo = false;
        m_lastTick = -1;
        m_lastSentTick = -1; // Reset so next demo starts fresh

        SendDemoStateEvent(false, lastKnownTick,
                           IsWithinHostCommandWindow()
                               ? cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_HOST_COMMAND
                               : cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_USER);
        stateEventSent = true;
        // The stop event above still reported the last engine pause truth; from
        // here (no demo file) is_paused falls back to echo state until the next
        // demo's first valid read re-seeds the cache.
        m_enginePausedValid.store(false);
    }

    int lastTick = m_lastTick.load();
    if (isPlayingDemo && currentTick != lastTick)
    {
        UpdateTick(currentTick);
        m_lastTick = currentTick;
    }

    // Engine-truth pause flip ("engine-pause-detection"): the headline of this
    // channel - a user pausing via the in-game demo UI surfaces here (outside the
    // suppression window -> ORIGIN_USER), as do host-driven flips (inside it).
    if (pauseFlipped)
    {
        std::cout << "[STATE] Engine pause flip: " << (snapshot.isPaused ? "paused" : "resumed") << std::endl;
        if (isPlayingDemo && currentTick >= 0)
        {
            // Key moment - force the new truth onto the tick stream: after a pause
            // ticks stop changing, so no regular update would ever carry it.
            SendTickUpdateIfNeeded(currentTick, true);
        }
        if (!stateEventSent)
        {
            SendDemoStateEvent(isPlayingDemo, currentTick,
                               IsWithinHostCommandWindow()
                                   ? cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_HOST_COMMAND
                                   : cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_USER);
            stateEventSent = true;
        }
    }

    // Demo path change without an accompanying transition this cycle (e.g. an
    // in-place demo change) gets its own DemoStateEvent; any event already sent
    // above carried the new path (the observed copy was refreshed first).
    if (pathChanged && !stateEventSent)
    {
        std::cout << "[STATE] Observed demo path change" << std::endl;
        SendDemoStateEvent(isPlayingDemo, currentTick,
                           IsWithinHostCommandWindow()
                               ? cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_HOST_COMMAND
                               : cs2_video_generator::DemoStateEvent_ChangeOrigin_ORIGIN_USER);
    }

    // Arrival / timeout evaluation for a pending acked seek ("seek-ack").
    CheckPendingSeekAck(isPlayingDemo, currentTick);
}

void Cs2PluginClient::StartPolling()
{
    if (m_pollingThread.joinable())
    {
        return; // Already running
    }

    m_shouldStopPolling = false;
    m_pollingThread = std::thread([this]() { EnginePollingLoop(); });
    std::cout << "[POLLING] Polling thread started" << std::endl;
}

void Cs2PluginClient::StopPolling()
{
    m_shouldStopPolling = true;
    if (m_pollingThread.joinable())
    {
        m_pollingThread.join();
        std::cout << "[POLLING] Polling thread stopped and joined" << std::endl;
    }
}

// present_capture.cpp: brackets the hooked-capture window on startmovie/endmovie. Inert
// unless the CSVG host is listening on the frame pipe.
void CsvgPresentCaptureOnEngineCommand(const char *cmd);

void Cs2PluginClient::DrainEngineCommandQueue(ISource2EngineToClient *engine)
{
    // Swap the backlog out under the lock, execute outside it: ExecuteClientCmd can
    // take arbitrarily long (the mock even sleeps in it) and enqueuers must not block.
    std::deque<QueuedEngineCommand> pending;
    {
        std::lock_guard<std::mutex> lock(m_engineCmdQueueMutex);
        pending.swap(m_engineCmdQueue);
    }
    for (const QueuedEngineCommand &entry : pending)
    {
        Log("[CS2 - Engine Execute] %s", entry.command.c_str());
        CsvgPresentCaptureOnEngineCommand(entry.command.c_str());
        engine->ExecuteClientCmd(0, entry.command.c_str(), true);
    }
}

void Cs2PluginClient::CheckStrandedEngineCommands()
{
    // Copy the offenders out under the lock; log outside it (Log writes a file).
    std::vector<std::string> stranded;
    size_t queueDepth = 0;
    {
        std::lock_guard<std::mutex> lock(m_engineCmdQueueMutex);
        queueDepth = m_engineCmdQueue.size();
        if (queueDepth == 0)
        {
            return;
        }
        const auto now = std::chrono::steady_clock::now();
        for (QueuedEngineCommand &entry : m_engineCmdQueue)
        {
            if (!entry.strandedLogged && now - entry.queuedAt >= kEngineCommandStrandedDeadline)
            {
                entry.strandedLogged = true;
                stranded.push_back(entry.command);
            }
        }
    }

    if (stranded.empty())
    {
        return;
    }

    // Loud and specific: the command text, how long it has waited, and the hook state
    // that explains why. There is no wire event that fits - CommandAckEvent needs the
    // originating command_id, which does not reach this layer - so this log is the
    // report; the host's own command timeouts surface the failure to the caller.
    Log("[gRPC] STRANDED: %d console command(s) have waited over %d s for the engine thread (queue depth %d, "
        "frame hook installed=%s, engine frames observed=%llu). CS2 has not reached a state where commands can "
        "execute.",
        (int)stranded.size(), (int)kEngineCommandStrandedDeadline.count(), (int)queueDepth,
        CsvgEngineFrameHookInstalled() ? "yes" : "NO",
        (unsigned long long)m_engineFrameCount.load(std::memory_order_relaxed));
    for (const std::string &cmd : stranded)
    {
        Log("[gRPC] STRANDED command: %s", cmd.c_str());
    }
}

void Cs2PluginClient::ExecuteEngineCommand(const std::string &command)
{
    // The real plugin ALWAYS queues: executing ExecuteClientCmd from this thread while
    // CS2 is mid-load crashes the engine (engine2.dll access violation), so a command
    // with no hook yet waits for one rather than taking a direct call that cannot be
    // made safe. The polling loop's install retry bounds that wait to CS2's client.dll
    // bring-up; anything past kEngineCommandStrandedDeadline is reported by name.
    if (CsvgEngineFrameHookInstalled() || CsvgCommandsRequireEngineThread())
    {
        bool hookInstalled = CsvgEngineFrameHookInstalled();
        {
            std::lock_guard<std::mutex> lock(m_engineCmdQueueMutex);
            m_engineCmdQueue.push_back({command, std::chrono::steady_clock::now(), false});
        }
        if (!hookInstalled && !m_queueUntilHookLogged.exchange(true))
        {
            Log("[gRPC] Engine frame hook not installed yet - holding console commands in the engine-thread "
                "queue (first held: %s). They execute as soon as the hook installs.",
                command.c_str());
        }
        return;
    }

    // Mock only (CSVG_MOCK_FRAME_HZ=0): no frame pump, and nothing thread-unsafe to
    // guard against, so execute from this thread. Any queued backlog drains first to
    // preserve ordering across the queued->direct boundary.
    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        std::cerr << "ExecuteEngineCommand: Engine not available, dropping: " << command << std::endl;
        return;
    }
    if (!m_directExecFallbackLogged.exchange(true))
    {
        Log("[gRPC] No engine frame pump on this binary - executing console commands directly from the calling "
            "thread");
    }
    DrainEngineCommandQueue(engine);
    Log("[CS2 - Direct Execute] %s", command.c_str());
    engine->ExecuteClientCmd(0, command.c_str(), true);
}

void Cs2PluginClient::OnEngineFrame()
{
    m_engineFrameCount.fetch_add(1, std::memory_order_relaxed);

    auto engine = m_engineGetter ? m_engineGetter() : nullptr;
    if (engine == nullptr)
    {
        return;
    }

    DrainEngineCommandQueue(engine);

    // Snapshot + state processing on the engine thread: these reads cannot race demo
    // teardown here. The guarded reader is reused so Windows keeps a single code
    // path (the SEH guard is a no-op in the non-faulting case).
    DemoStateSnapshot snapshot;
    if (ReadDemoStateSnapshot(engine, &snapshot))
    {
        m_firstSnapshotOk.store(true);
        ProcessEngineSnapshot(snapshot);
    }

    // Second drain: snapshot processing itself enqueues commands (tick-scheduled
    // batches, the PlayDemoTickRange auto-pause, a seek arrival's pause_after_seek).
    // Draining again keeps those tick-exact - executed in the very frame their tick
    // was observed - instead of one frame late.
    DrainEngineCommandQueue(engine);
}

void Cs2PluginClient::OfferGameEvent(const char *eventName, const char *jsonPayload)
{
    // Cheap early-out on the engine thread: disabled means one relaxed atomic load.
    if (!m_gameEventsEnabled.load(std::memory_order_relaxed) || eventName == nullptr)
    {
        return;
    }

    {
        std::lock_guard<std::mutex> lock(m_gameEventFilterMutex);
        if (!m_gameEventFilter.empty() &&
            std::find(m_gameEventFilter.begin(), m_gameEventFilter.end(), eventName) == m_gameEventFilter.end())
        {
            return;
        }
    }

    uint64_t dropCountToLog = 0;
    {
        std::lock_guard<std::mutex> lock(m_gameEventQueueMutex);
        if (m_gameEventQueue.size() >= kGameEventQueueCap)
        {
            // Telemetry is best-effort: drop the oldest event rather than ever making
            // the engine thread wait on the host.
            m_gameEventQueue.pop_front();
            ++m_gameEventDropCount;
            if (m_gameEventDropCount == 1 || m_gameEventDropCount % 256 == 0)
            {
                dropCountToLog = m_gameEventDropCount;
            }
        }
        m_gameEventQueue.emplace_back(eventName, jsonPayload != nullptr ? jsonPayload : "");
    }
    m_gameEventQueueCv.notify_one();

    if (dropCountToLog != 0)
    {
        Log("[GameEvents] Telemetry queue overflow - dropped oldest event (total dropped: %llu)",
            static_cast<unsigned long long>(dropCountToLog));
    }
}

void Cs2PluginClient::GameEventSenderLoop()
{
    std::cout << "[GameEvents] Sender thread started" << std::endl;
    while (true)
    {
        std::pair<std::string, std::string> item;
        {
            std::unique_lock<std::mutex> lock(m_gameEventQueueMutex);
            m_gameEventQueueCv.wait(lock, [this] { return m_stopGameEventSender.load() || !m_gameEventQueue.empty(); });
            if (m_stopGameEventSender.load())
            {
                break; // Undelivered events are dropped - the stream is going away anyway
            }
            item = std::move(m_gameEventQueue.front());
            m_gameEventQueue.pop_front();
        }

        cs2_video_generator::CsProcessEvent event;
        cs2_video_generator::GameEventTelemetryEvent *telemetry = event.mutable_game_event_telemetry();
        telemetry->set_event_name(item.first);
        telemetry->set_json_payload(item.second);
        if (!WriteEvent(event))
        {
            std::cerr << "Failed to send game event telemetry (stream unavailable or closing)" << std::endl;
        }
    }
    std::cout << "[GameEvents] Sender thread stopped" << std::endl;
}

void Cs2PluginClient::EnsureGameEventSenderStarted()
{
    // The start (reader thread, on first enable) and the stop (Stop / destructor) run
    // on different threads and both touch m_gameEventSenderThread.
    std::lock_guard<std::mutex> lock(m_gameEventSenderMutex);
    if (m_gameEventSenderThread.joinable() || m_stopGameEventSender.load())
    {
        return; // Already running, or the session is tearing down
    }
    m_gameEventSenderThread = std::thread([this]() { GameEventSenderLoop(); });
}

void Cs2PluginClient::StopGameEventSender()
{
    m_stopGameEventSender.store(true);
    m_gameEventQueueCv.notify_all();
    std::lock_guard<std::mutex> lock(m_gameEventSenderMutex);
    if (m_gameEventSenderThread.joinable())
    {
        m_gameEventSenderThread.join();
    }
}
