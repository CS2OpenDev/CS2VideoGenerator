/**
 * @file Cs2PluginClient.h
 * @brief CS2 game plugin gRPC client interface
 */

#pragma once

#include "cs2-video-generator.grpc.pb.h"
#include "cs2-video-generator.pb.h"
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <grpcpp/grpcpp.h>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

class ISource2EngineToClient;

// Lazy: the engine may not be available immediately at startup.
using EngineGetterFunc = std::function<ISource2EngineToClient *()>;

/**
 * @brief Snapshot of engine demo-playback state read in a single pass.
 *
 * Deliberately POD throughout (including the fixed char buffer for the demo path):
 * on Windows the polling thread fills it inside a __try frame, which forbids locals
 * with unwinding semantics, and the engine-owned GetDemoFilePath chars must be
 * copied INSIDE the guard before the demo player can be torn down under us. The
 * engine-thread frame hook fills the same struct; no guard needed there, teardown
 * happens on that thread.
 */
struct DemoStateSnapshot
{
    bool isPlayingDemo;      ///< Whether the engine is currently playing a demo.
    int currentTick;         ///< Current demo tick, or -1 when not playing / unavailable.
    bool isPaused;           ///< IDemoFile::IsDemoPaused; meaningful only when pausedValid.
    bool pausedValid;        ///< Whether isPaused was actually read this cycle (demo file present).
    char demoFilePath[1024]; ///< Bounded copy of GetDemoFilePath ("" = none/null).
};

using TickUpdateCallback = std::function<void(int currentTick, bool isPaused)>;
using PlaybackStatusCallback = std::function<void(int previousStatus, int newStatus, const std::string &errorMessage)>;
using TickActionCallback = std::function<void(int actualTick)>;

/**
 * @class Cs2PluginClient
 * @brief gRPC client for CS2 game plugin communication with integrated demo playback orchestration
 *
 * This class manages bidirectional streaming communication between the CS2 game
 * plugin and the .NET Core server. It forwards game events to the server and
 * receives control commands to manipulate demo playback.
 *
 * Demo playback is orchestrated directly through ISource2EngineToClient; there is no
 * separate IGameController layer.
 *
 * @note This class maintains a persistent gRPC stream connection
 * @warning The engine pointer must remain valid for the lifetime of this object
 *
 * @par Communication Flow:
 * - Client -> Server: TickUpdateEvent, DemoPlaybackStatusChange, DemoStateEvent, CommandAckEvent
 * - Server -> Client: LoadDemo, ResumeDemo, PauseDemo, CloseDemo, SetDemoTick, PlayDemoTickRange,
 *   SetDemoTimescale, QueryDemoState
 *
 * @see ISource2EngineToClient for engine interface
 * @see cs2_video_generator.proto for message definitions
 */
class Cs2PluginClient
{
  private:
    std::unique_ptr<cs2_video_generator::Cs2GameService::Stub> stub_; ///< gRPC service stub
    std::shared_ptr<grpc::Channel> channel_;                          ///< gRPC channel
    std::string traceparent_;                                         ///< W3C traceparent for distributed tracing
    grpc::ClientReaderWriter<cs2_video_generator::CsProcessEvent,
                             cs2_video_generator::CsControlRequest> *stream_; ///< Bidirectional stream
    grpc::ClientContext context_; ///< Owned context to allow cancellation on shutdown

    // grpc::ClientReaderWriter permits concurrent read and write, but the write side is
    // NOT thread-safe: every Write() and the terminal Finish() must be serialized. Tick
    // updates (polling thread), status changes (reader thread's command handlers), and
    // process status (startup + the READY notifier) all write, and StartGameSession calls
    // Finish() at teardown, so all of them take m_streamMutex. m_streamClosing is set before
    // Finish() (and in Stop) so no Write() runs concurrently with, or after, Finish();
    // concurrent write-side ops on the internal completion queue otherwise trip a gRPC CHECK
    // (grpc_cq_begin_op) and abort the process. Stop() only sets the flag and TryCancel()s;
    // it must not issue WritesDone()/Finish() itself (that would race StartGameSession's Finish).
    std::mutex m_streamMutex;                 ///< Serializes all write-side stream operations (incl. Finish)
    std::atomic<bool> m_streamClosing{false}; ///< Set at shutdown; blocks further writes

    EngineGetterFunc m_engineGetter;    ///< Function to retrieve engine interface (may return null)
    std::atomic<int> m_currentTick{-1}; ///< Current demo tick (atomic for thread safety)
    std::atomic<bool> m_isPaused{true}; ///< Echo pause state: the last host-commanded value (see EffectivePausedState)
    std::atomic<int> m_playbackStatus{0}; ///< Current playback status (DemoPlaybackStatus enum)

    // Engine-truth pause ("engine-pause-detection"). The polling thread reads
    // IDemoFile::IsDemoPaused (win slot 012 / linux slot 013; both declared in
    // cdll_interfaces.h - the Linux shift is baked into the class layout) in the same
    // guarded snapshot as GetDemoTick. m_enginePausedValid is false until the first
    // successful read for the current demo and is reset on demo stop; while it is
    // false, is_paused fields fall back to the m_isPaused echo value.
    std::atomic<bool> m_enginePaused{false};      ///< Last polled IDemoFile::IsDemoPaused value
    std::atomic<bool> m_enginePausedValid{false}; ///< Whether m_enginePaused holds a real engine read

    // Demo identity ("demo-identity"). The polling thread copies
    // ISource2EngineToClient::GetDemoFilePath (slot 043) into the snapshot each cycle;
    // the latest copy lives here for DemoStateEvent population, path-change detection,
    // and HandleLoadDemo's load-completion check. Guarded by a mutex (std::string is
    // not atomic; readers are the reader thread and the polling thread).
    std::mutex m_observedPathMutex; ///< Guards m_observedDemoPath
    std::string m_observedDemoPath; ///< Last observed GetDemoFilePath value ("" = none/unavailable)

    // Scheduled tick actions ("tick-scheduled-commands" + PlayDemoTickRange auto-pause).
    // Fired with mirv_cmd/CSDM half-open-interval semantics: an action at tick T fires on
    // the first observation where lastObservedTick < T <= currentTick, so seeks/jumps that
    // step over T cannot skip it. One-shot: fired entries are erased. Backward tick
    // movement (seek/reload) fires nothing - the next forward progression from the new
    // (lower) tick resumes normal crossing checks. Entries are either an internal callback
    // (auto-pause) or a host-scheduled console command batch; host entries survive
    // PlayDemoTickRange's internal clear but are dropped with everything else on LoadDemo
    // (a new demo invalidates the tick space).
    struct ScheduledTickAction
    {
        int id = 0;                        ///< Unique action id (logging/diagnostics)
        bool hostOwned = false;            ///< true = registered via ScheduleCommandsAtTickCommand
        std::vector<std::string> commands; ///< Console commands to execute (host entries)
        TickActionCallback callback;       ///< Callback to invoke (internal entries)
    };
    std::multimap<int, ScheduledTickAction> m_tickActions; ///< tick -> scheduled actions (multiple per tick allowed)
    std::mutex m_tickActionsMutex;                         ///< Mutex protecting m_tickActions
    std::atomic<int> m_nextActionId{1};                    ///< Counter for generating unique action IDs

    std::atomic<int> m_tickUpdateFrequency{500}; ///< How often to send tick updates (default: every 500 ticks)
    std::atomic<int> m_lastSentTick{-1};         ///< Last tick value that was sent to the server

    // Demo timescale tracking ("timescale-set" capability). There is no engine readback in
    // this version: DemoStateEvent.demo_timescale reports the last host-set value only, and
    // is omitted from the wire until the host has set one.
    std::atomic<float> m_demoTimescale{1.0f}; ///< Last host-set demo_timescale value
    std::atomic<bool> m_timescaleSet{false};  ///< Whether the host has ever set a timescale

    // Host-command suppression window (DemoStateEvent origin attribution). Playback-affecting
    // command handlers stamp this monotonic timestamp; when the polling thread observes a
    // playing-state transition within kHostCommandWindow of the stamp it attributes the change
    // to ORIGIN_HOST_COMMAND, otherwise to ORIGIN_USER.
    static constexpr std::chrono::nanoseconds kHostCommandWindow{std::chrono::seconds(3)};
    std::atomic<int64_t> m_lastPlaybackCommandNs{
        0}; ///< steady_clock ns of the last playback-affecting command (0 = none)

    // Arrival-verified seeks ("seek-ack"). A SetDemoTickCommand that carries a
    // command_id gets its CommandAckEvent deferred until the polling thread observes
    // the engine arrive at the target (see CheckPendingSeekAck for the predicate).
    // One pending seek at a time: a second acked seek supersedes the first, which is
    // failed with an error ack. Deliberately separate from m_tickActions - that map is
    // one-action-per-tick, and a pending seek colliding with a PlayDemoTickRange
    // auto-pause scheduled at the same tick must not drop either.
    static constexpr int kSeekArrivalToleranceTicks = 16;          ///< +-tick window that counts as "arrived"
    static constexpr std::chrono::seconds kSeekArrivalTimeout{30}; ///< Arrival deadline; expiry = failure ack
    struct PendingSeekAck
    {
        bool active = false;            ///< Whether a seek ack is pending
        uint64_t commandId = 0;         ///< CsControlRequest.command_id to ack on completion
        int targetTick = 0;             ///< demo_gototick target
        bool awaitingDescent = false;   ///< Backward seek: arrival check armed only after the tick is first
                                        ///< observed at/below target+tolerance (demo reload restarts low)
        bool hasPauseAfterSeek = false; ///< Whether pause_after_seek was present on the command
        bool pauseAfterSeek = false;    ///< pause_after_seek value (applied after arrival)
        std::chrono::steady_clock::time_point deadline{}; ///< Arrival timeout deadline
    };
    PendingSeekAck m_pendingSeekAck;  ///< The single pending-seek-ack slot
    std::mutex m_pendingSeekAckMutex; ///< Guards m_pendingSeekAck (reader thread registers, polling thread completes)

    std::thread m_pollingThread;                  ///< Thread that polls engine state
    std::atomic<bool> m_shouldStopPolling{false}; ///< Signal to stop polling thread
    std::atomic<bool> m_wasPlayingDemo{false};    ///< Previous demo playing state (for change detection)
    std::atomic<int> m_lastTick{-1};              ///< Previous tick value (for change detection)

    // Engine-thread frame hook (FrameStageNotify, FRAME_START). ExecuteClientCmd is not
    // thread-safe from background threads (the suspected cause of intermittent CS2
    // crashes; CSDM hooks FrameStageNotify for exactly this reason), so commands are
    // queued and drained by OnEngineFrame on the engine thread. Once the hook is
    // installed the engine snapshot is also read there - where demo teardown cannot
    // race it - and the polling thread goes permanently passive.
    //
    // The switch is installation (a sticky, binary-published fact via
    // CsvgEngineFrameHookInstalled), deliberately not frame recency: a real demo load
    // takes 10-15 s and may not render meanwhile, and a recency-based switch would hand
    // snapshot duty back to a background thread during exactly the load/teardown window
    // the SEH guard exists for. An installed-but-stalled hook simply defers commands
    // until frames resume; if they never do, CS2 is gone and the host's own timeouts
    // (60 s load, 35 s seek ack) surface it.
    //
    // What happens before installation is per-binary, via CsvgCommandsRequireEngineThread:
    // the real plugin queues and waits (executing ExecuteClientCmd from a background
    // thread into a mid-load engine crashes CS2 outright, so there is no safe "legacy
    // path" to degrade to - the polling loop's hook-install retry makes the wait a few
    // seconds at boot); the mock keeps direct execution, which is the leg
    // CSVG_MOCK_FRAME_HZ=0 exists to exercise.
    struct QueuedEngineCommand
    {
        std::string command;                            ///< The console command text
        std::chrono::steady_clock::time_point queuedAt; ///< When it entered the queue (stranding deadline)
        bool strandedLogged = false;                    ///< Whether it has already been reported as stranded
    };
    /// How long a command may sit unexecuted before it is reported as stranded. Sized
    /// well above the observed client.dll bring-up window so a normal boot is silent.
    static constexpr std::chrono::seconds kEngineCommandStrandedDeadline{10};
    std::mutex m_engineCmdQueueMutex;                 ///< Guards m_engineCmdQueue
    std::deque<QueuedEngineCommand> m_engineCmdQueue; ///< Commands awaiting engine-thread execution
    std::atomic<uint64_t> m_engineFrameCount{0};      ///< Total observed engine frames (readiness signal)
    std::mutex m_snapshotProcessMutex; ///< Serializes ProcessEngineSnapshot between frame hook and polling thread
    std::atomic<bool> m_firstSnapshotOk{false}; ///< First successful engine state read observed (readiness signal)
    std::atomic<bool> m_directExecFallbackLogged{false}; ///< One-shot log guard for the mock's direct-exec path
    std::atomic<bool> m_queueUntilHookLogged{false};     ///< One-shot log guard for the queue-until-hooked notice

    // Game-event telemetry ("game-event-telemetry", off by default). The per-binary hook
    // (real plugin: CGameEventManager FireEventClientSide vtable patch; mock: stdin
    // injection) calls OfferGameEvent from the engine thread; events matching the filter
    // are pushed to a bounded queue and forwarded to the host by a dedicated sender
    // thread so serialization/stream writes never block FrameStageNotify. Drop-oldest on
    // overflow with a counter (telemetry is best-effort, playback control is not).
    static constexpr size_t kGameEventQueueCap = 256;                 ///< Bounded queue size (drop-oldest beyond)
    std::atomic<bool> m_gameEventsEnabled{false};                     ///< Subscription master switch (default off)
    std::mutex m_gameEventFilterMutex;                                ///< Guards m_gameEventFilter
    std::vector<std::string> m_gameEventFilter;                       ///< Subscribed event names (empty = all)
    std::mutex m_gameEventQueueMutex;                                 ///< Guards m_gameEventQueue
    std::condition_variable m_gameEventQueueCv;                       ///< Wakes the sender thread
    std::deque<std::pair<std::string, std::string>> m_gameEventQueue; ///< {event name, JSON payload}
    uint64_t m_gameEventDropCount = 0;              ///< Events dropped on overflow (guarded by m_gameEventQueueMutex)
    std::mutex m_gameEventSenderMutex;              ///< Serializes sender-thread start/stop
    std::thread m_gameEventSenderThread;            ///< Lazily started on first enable
    std::atomic<bool> m_stopGameEventSender{false}; ///< Signal to stop the sender thread

    /**
     * @brief Internal polling loop that monitors engine state
     *
     * Runs at ~120Hz (faster than 64Hz tick rate) in a separate thread. While the
     * engine-thread frame hook is alive it stays passive (OnEngineFrame owns snapshot
     * duty); otherwise it reads the guarded snapshot and feeds ProcessEngineSnapshot,
     * exactly as before the frame hook existed.
     */
    void EnginePollingLoop();

    /**
     * @brief Derives state transitions/events from one engine snapshot observation
     * @param snapshot The state read this cycle (polling thread or engine frame hook)
     *
     * Single consumer of snapshots: demo start/stop edges, tick changes (scheduled
     * actions + tick updates), engine pause flips, demo path changes, and pending
     * seek-ack evaluation. Serialized by m_snapshotProcessMutex because the frame hook
     * and the polling thread can hand over duty concurrently at liveness boundaries.
     */
    void ProcessEngineSnapshot(const DemoStateSnapshot &snapshot);

    /**
     * @brief Executes queued console commands on the current thread, oldest first
     * @param engine The engine interface to execute against
     *
     * Called by OnEngineFrame (engine thread) and by the mock's direct-exec path so
     * ordering is preserved across the queued->direct boundary.
     */
    void DrainEngineCommandQueue(ISource2EngineToClient *engine);

    /**
     * @brief Reports console commands that have waited past kEngineCommandStrandedDeadline
     *
     * Called from the polling loop. On the real plugin a queued command executes only
     * once the FrameStageNotify hook is installed and the engine renders a frame; if
     * neither has happened in 10 s something is wrong (client.dll never came up, the
     * vtable slot moved, the engine never started rendering) and the operator needs to
     * see it by name rather than watch a silent stall. Logs once per command per batch:
     * a command already reported stays quiet until it drains or a new one goes overdue.
     */
    void CheckStrandedEngineCommands();

    /**
     * @brief Updates the current tick from a state snapshot observation
     * @param tick The new tick value from the demo file
     *
     * Called by ProcessEngineSnapshot when the demo tick changes.
     * Processes scheduled actions and sends tick update to server.
     */
    void UpdateTick(int tick);

    /**
     * @brief Fires scheduled actions whose tick lies in the half-open interval
     *        (lastObservedTick, currentTick]
     * @param lastObservedTick The previously observed tick (-1 = none for this demo)
     * @param currentTick The tick observed this cycle
     *
     * mirv_cmd/CSDM semantics: ticks can be skipped between observations, so every
     * action in the crossed interval fires (in tick order); a backward movement
     * (currentTick <= lastObservedTick) fires nothing. Fired entries are erased.
     */
    void ProcessScheduledActions(int lastObservedTick, int currentTick);

    /**
     * @brief Schedules an internal callback to fire when the observed tick crosses the given tick
     * @param tick The tick at which to execute the callback
     * @param callback The action to execute when the tick is crossed
     * @return A unique identifier for the scheduled action
     */
    int ScheduleAtTick(int tick, TickActionCallback callback);

    /**
     * @brief Schedules a host-owned console command batch ("tick-scheduled-commands")
     * @param tick The tick whose crossing executes the batch (on the engine thread)
     * @param commands Console commands, executed in order
     * @return A unique identifier for the scheduled action
     */
    int ScheduleCommandsAtTick(int tick, std::vector<std::string> commands);

    /**
     * @brief Which scheduled-action entries a clear operation removes.
     *
     * LoadDemo clears All (a new demo invalidates the tick space);
     * PlayDemoTickRange clears InternalOnly (its stale auto-pause must go, but it
     * must not clobber host-scheduled command batches);
     * ClearScheduledCommandsCommand clears HostOnly.
     */
    enum class ScheduledActionScope
    {
        All,
        InternalOnly,
        HostOnly
    };

    void ClearScheduledActions(ScheduledActionScope scope);

    /** @brief Starts the engine polling thread. Called from the constructor. */
    void StartPolling();

    /** @brief Stops and joins the engine polling thread. Called from the destructor and Stop(). */
    void StopPolling();

    /**
     * @brief Processes incoming control requests from the server
     * @param request The control request message containing a command
     *
     * Routes control commands to the appropriate handler method based
     * on the request type (oneof field in protobuf).
     */
    void HandleControlRequest(const cs2_video_generator::CsControlRequest &request);

    /**
     * @brief Outcome of a command handler, used to build the CommandAckEvent reply
     *        when the enclosing CsControlRequest carried a command_id ("command-ack").
     */
    struct CommandOutcome
    {
        bool success = true;      ///< Whether the command was applied
        std::string errorMessage; ///< Failure detail (empty on success)
        bool ackDeferred = false; ///< True when the handler took ownership of the ack itself
                                  ///< (arrival-verified seeks: the polling thread completes it)
    };

    CommandOutcome HandleLoadDemo(const cs2_video_generator::LoadDemoCommand &cmd);

    CommandOutcome HandleResumeDemo(const cs2_video_generator::ResumeDemoCommand &cmd);

    CommandOutcome HandlePauseDemo(const cs2_video_generator::PauseDemoCommand &cmd);

    CommandOutcome HandleCloseDemo(const cs2_video_generator::CloseDemoCommand &cmd);

    /**
     * @brief Handles SetDemoTick command
     * @param cmd The seek command
     * @param commandId The enclosing request's command_id (0 when absent)
     *
     * Two behaviors ("seek-ack" capability):
     * - command_id present: arrival-verified. Registers the pending-seek-ack slot and
     *   returns with ackDeferred - no target-tick pre-echo, no immediate
     *   pause_after_seek; the polling thread completes the ack (and applies
     *   pause_after_seek) once the engine is observed at the target, or fails it on
     *   timeout. The reader thread never blocks waiting for arrival.
     * - bare/legacy (no command_id): previous behavior - force-send the target tick
     *   immediately and apply pause_after_seek as immediate intent.
     */
    CommandOutcome HandleSetDemoTick(const cs2_video_generator::SetDemoTickCommand &cmd, uint64_t commandId);

    CommandOutcome HandlePlayDemoTickRange(const cs2_video_generator::PlayDemoTickRangeCommand &cmd);

    /** @brief Handles SetSpectatorTarget command - sets which player slot to spectate */
    CommandOutcome HandleSetSpectatorTarget(const cs2_video_generator::SetSpectatorTargetCommand &cmd);

    /** @brief Handles SetDemoOptions command - configures xray, hud, true view, etc. */
    CommandOutcome HandleSetDemoOptions(const cs2_video_generator::SetDemoOptionsCommand &cmd);

    CommandOutcome HandleSetTickUpdateFrequency(const cs2_video_generator::SetTickUpdateFrequencyCommand &cmd);

    /** @brief Handles SetDemoTimescale command - executes demo_timescale, remembers the value */
    CommandOutcome HandleSetDemoTimescale(const cs2_video_generator::SetDemoTimescaleCommand &cmd);

    /**
     * @brief Handles QueryDemoState command - replies with a DemoStateEvent snapshot
     * @param cmd The (empty) query command
     * @param commandId The enclosing request's command_id (0 when absent); carried as
     *        DemoStateEvent.in_reply_to_command_id so the orchestrator can correlate
     */
    CommandOutcome HandleQueryDemoState(const cs2_video_generator::QueryDemoStateCommand &cmd, uint64_t commandId);

    /**
     * @brief Handles ScheduleCommandsAtTick - registers a host-owned console command batch
     *        ("tick-scheduled-commands"). The ack (when requested) confirms registration,
     *        not execution; the stream's in-order delivery guarantees the schedule is
     *        armed before any later command (e.g. ResumeDemo) is processed.
     */
    CommandOutcome HandleScheduleCommandsAtTick(const cs2_video_generator::ScheduleCommandsAtTickCommand &cmd);

    /** @brief Handles ClearScheduledCommands - drops unfired host-scheduled command batches */
    CommandOutcome HandleClearScheduledCommands(const cs2_video_generator::ClearScheduledCommandsCommand &cmd);

    /**
     * @brief Handles ExecuteConsoleCommands - queues a console command batch for the
     *        engine thread ("console-exec"). The immediate counterpart of
     *        HandleScheduleCommandsAtTick: no tick condition, no demo required. Commands
     *        are blind (the engine returns nothing), so the ack - when requested -
     *        confirms queuing, never engine acceptance.
     */
    CommandOutcome HandleExecuteConsoleCommands(const cs2_video_generator::ExecuteConsoleCommandsCommand &cmd);

    /**
     * @brief Handles SetGameEventSubscription ("game-event-telemetry", default off).
     *        Enabling lazily installs the per-binary game-event hook (never at load);
     *        a failed install fails the command. Disabling flips the master switch and
     *        leaves the (cheap, pass-through) hook in place.
     */
    CommandOutcome HandleSetGameEventSubscription(const cs2_video_generator::SetGameEventSubscriptionCommand &cmd);

    /**
     * @brief Routes a console command to the engine thread (or the legacy fallback)
     * @param command The console command to execute
     *
     * While the frame hook is alive the command is queued for the next FRAME_START
     * drain (ExecuteClientCmd is not thread-safe off the engine thread). When no
     * frames are arriving - hook not installed yet, vtable drift, engine stall - any
     * queued backlog is drained and the command executed directly from this thread,
     * preserving order and the pre-frame-hook behavior (warn-not-block: commands are
     * never stranded behind a dead hook).
     */
    void ExecuteEngineCommand(const std::string &command);

    /**
     * @brief Sender loop for game-event telemetry: drains the bounded event queue and
     *        writes GameEventTelemetryEvents to the stream, off the engine thread.
     */
    void GameEventSenderLoop();

    void EnsureGameEventSenderStarted();

    /** @brief Stops and joins the game-event sender thread (idempotent) */
    void StopGameEventSender();

    /**
     * @brief Sends tick update if needed based on frequency setting
     * @param tick The current tick value
     * @param force If true, sends update regardless of frequency (used for key moments)
     *
     * Key moments that force updates include: pause, resume, seek, end of play range.
     */
    void SendTickUpdateIfNeeded(int tick, bool force = false);

    /**
     * @brief Checks if the gRPC channel is ready
     * @param timeout Maximum time to wait for connection
     * @return true if channel is ready, false if timeout occurred
     */
    bool WaitForChannelReady(std::chrono::seconds timeout);

    /**
     * @brief Serializes a single write to the bidirectional stream.
     * @param event The event to write.
     * @return true if the event was written; false if the stream is unavailable, shutting
     *         down, or the write failed.
     *
     * All write-side stream access funnels through here: it takes m_streamMutex (so writes
     * never overlap WritesDone or each other) and refuses to write once m_streamClosing is
     * set. See the m_streamMutex/m_streamClosing member comments for why.
     */
    bool WriteEvent(const cs2_video_generator::CsProcessEvent &event);

    /**
     * @brief Sends a tick update event to the server
     * @param currentTick The current demo playback tick number
     * @param isPaused Whether the demo playback is currently paused
     *
     * @note Silently returns if stream is not initialized
     * @warning Write failures are logged but do not throw exceptions
     */
    void SendTickUpdate(int currentTick, bool isPaused);

    /**
     * @brief Sends a demo playback status change event to the server
     * @param newStatus The new playback status (DemoPlaybackStatus enum value)
     * @param errorMessage Optional error message (empty string if no error)
     *
     * Automatically uses the current m_playbackStatus as previousStatus, then updates
     * m_playbackStatus to newStatus after sending the event.
     *
     * @note Valid status values are defined in DemoPlaybackStatusChange_DemoPlaybackStatus enum
     * @see cs2_video_generator::DemoPlaybackStatusChange_DemoPlaybackStatus
     */
    void SendPlaybackStatusChange(int newStatus, const std::string &errorMessage = "");

    /**
     * @brief Sends process lifecycle status to the server (e.g., INITIALIZING, READY)
     * @param status Process status enum value
     * @param errorMessage Optional error message for error states
     *
     * Every process status also carries the version fields and the build-time
     * capability token list (see SendProcessStatus in the .cpp for the token set).
     */
    void SendProcessStatus(cs2_video_generator::Cs2ProcessStatus_ProcessStatus status,
                           const std::string &errorMessage = "");

    /**
     * @brief Sends a CommandAckEvent for a command_id-carrying control request
     * @param commandId The correlation id echoed from CsControlRequest.command_id
     * @param success Whether the command handler succeeded
     * @param errorMessage Failure detail (omitted from the wire when empty)
     * @param observedDemoTick For arrival-verified seeks ("seek-ack"): the tick the
     *        engine was actually observed at when the seek completed; negative =
     *        omitted from the wire (non-seek commands, failures)
     */
    void SendCommandAck(uint64_t commandId, bool success, const std::string &errorMessage = "",
                        int observedDemoTick = -1);

    /**
     * @brief Sends a DemoStateEvent transition/snapshot ("demo-state-events" capability)
     * @param isPlayingDemo Whether the engine is (now) playing a demo
     * @param demoTick Demo tick to report; negative values are omitted from the wire
     * @param origin Attribution for the change (host command vs user)
     * @param inReplyToCommandId QueryDemoState correlation id; 0 = not a query reply
     *
     * is_paused comes from EffectivePausedState() (engine truth once polled,
     * "engine-pause-detection"; echo fallback otherwise), demo_file_path from the last
     * observed GetDemoFilePath copy ("demo-identity"; empty = no demo), and
     * demo_timescale from the last host-set value when one exists.
     */
    void SendDemoStateEvent(bool isPlayingDemo, int demoTick, cs2_video_generator::DemoStateEvent_ChangeOrigin origin,
                            uint64_t inReplyToCommandId = 0);

    /**
     * @brief The pause state reported on the wire (tick updates + demo state events)
     * @return Engine truth (m_enginePaused) once the polling thread has a valid
     *         IDemoFile::IsDemoPaused read for the current demo; the m_isPaused echo
     *         value (last host-commanded state) otherwise
     */
    bool EffectivePausedState() const;

    /**
     * @brief Returns a copy of the last observed GetDemoFilePath value ("" = none)
     */
    std::string ObservedDemoPath();

    /**
     * @brief Stores the latest polled GetDemoFilePath copy
     * @param path The path observed this polling cycle (never null; "" = none)
     * @return true when the stored value changed (drives path-change DemoStateEvents)
     */
    bool UpdateObservedDemoPath(const char *path);

    /**
     * @brief Registers (or supersedes) the pending-seek-ack slot ("seek-ack")
     * @param commandId The correlation id to ack on completion (non-zero)
     * @param targetTick The demo_gototick target
     * @param hasPauseAfterSeek Whether the command carried pause_after_seek
     * @param pauseAfterSeek pause_after_seek value, applied after arrival
     *
     * Called by HandleSetDemoTick (reader thread) before the demo_gototick is issued,
     * so the backward/forward classification sees the pre-seek tick. If a seek ack is
     * already pending it is failed with a "superseded" error ack.
     */
    void RegisterPendingSeekAck(uint64_t commandId, int targetTick, bool hasPauseAfterSeek, bool pauseAfterSeek);

    /**
     * @brief Evaluates the pending-seek-ack slot against this cycle's observation
     * @param isPlayingDemo Whether the engine reported a demo playing this cycle
     * @param observedTick The observed demo tick (-1 = unavailable)
     *
     * Called from the polling thread every cycle. Arrival predicate (tolerance = 16):
     * - Forward seeks arm immediately; backward seeks (target at/below the tick at
     *   registration) first await "descent": an observation at/below target+16. A
     *   backward demo_gototick reloads the demo - the tick restarts near 0 and rises
     *   back to the target - while the mock jumps straight to it; both satisfy the
     *   descent condition (transient !isPlayingDemo cycles during the reload are
     *   skipped, not treated as failure).
     * - Once armed, the engine has arrived at the first observation >= target-16
     *   (covers landing inside the +-16 window as well as fast playback stepping over
     *   it between polls).
     * On arrival: pause_after_seek is applied (demo_pause/demo_resume, with the
     * suppression-window stamp), a forced tick update with the observed tick goes out,
     * and the CommandAckEvent carries observed_demo_tick. Past the 30 s deadline the
     * ack is failed instead; a demo stop does not fail it early (backward-seek reloads
     * pass through a stopped state).
     */
    void CheckPendingSeekAck(bool isPlayingDemo, int observedTick);

    /**
     * @brief Stamps the monotonic clock: "a playback-affecting host command just executed"
     *
     * Called by the handlers that can flip the engine's playing state (LoadDemo, CloseDemo,
     * Resume, Pause, SetDemoTick, PlayDemoTickRange) so the polling thread can attribute
     * observed transitions; see IsWithinHostCommandWindow.
     */
    void NotePlaybackCommand();

    /**
     * @brief Whether a playback-affecting host command executed within kHostCommandWindow
     * @return true if the last NotePlaybackCommand stamp is recent enough
     */
    bool IsWithinHostCommandWindow() const;

  public:
    /**
     * @brief Constructs a CS2 plugin gRPC client
     *
     * Initializes the gRPC stub for communication with the .NET server.
     * The client will orchestrate demo playback using the provided engine getter function.
     *
     * @param channel Shared pointer to the gRPC channel for communication
     * @param engineGetter Function that returns the engine interface (may return null if not yet available)
     * @param traceparent Optional W3C traceparent header for distributed tracing
     *
     * @note The engine getter function allows lazy retrieval of the engine interface,
     *       which may not be available immediately at startup
     *
     * @par Example Usage:
     * @code
     * auto channel = grpc::CreateChannel("localhost:50051", grpc::InsecureChannelCredentials());
     * Cs2PluginClient client(channel, &GetEngine, "00-abc123-def456-01");
     * @endcode
     */
    explicit Cs2PluginClient(std::shared_ptr<grpc::Channel> channel, EngineGetterFunc engineGetter,
                             const std::string &traceparent = "");

    ~Cs2PluginClient();

    /**
     * @brief Establishes and maintains a bidirectional gRPC session
     *
     * Creates a streaming RPC connection to the game service server. The method:
     * - Establishes the bidirectional stream
     * - Spawns a reader thread to handle incoming control requests
     * - Sends an initial tick update event
     * - Blocks until the connection is closed
     *
     * @note This method blocks the calling thread until the session ends
     * @note Errors are logged to std::cerr but do not throw exceptions
     *
     * @par Threading:
     * A separate reader thread is spawned to handle incoming commands while
     * the main thread blocks waiting for the session to end.
     *
     * @see HandleControlRequest for processing incoming commands
     * @see SendTickUpdate for sending game state updates
     * @see SendPlaybackStatusChange for sending status transitions
     */
    void StartGameSession();

    /**
     * @brief Gracefully stop the gRPC session.
     * Cancels the client context and attempts a WritesDone to unblock reads.
     */
    void Stop();

    /**
     * @brief Engine-thread frame callback (FrameStageNotify FRAME_START).
     *
     * Called by the host binary's frame hook (real plugin: the patched
     * Source2Client002::FrameStageNotify; mock: a pump thread standing in for the
     * engine thread). Stamps frame liveness, drains the queued console commands via
     * ExecuteClientCmd, then reads the engine state snapshot and feeds
     * ProcessEngineSnapshot - all on the engine thread, where none of it can race
     * demo teardown. Must stay cheap: it runs once per rendered frame.
     */
    void OnEngineFrame();

    /**
     * @brief Offers a game event to the telemetry pipeline ("game-event-telemetry")
     * @param eventName The game event name (e.g. "player_death")
     * @param jsonPayload The event's KV3 payload serialized as JSON
     *
     * Called by the per-binary game-event hook (engine thread) or the mock injection
     * channel. Cheap when disabled (single atomic load). Enabled + filter-matching
     * events are queued (bounded, drop-oldest) for the sender thread; this never
     * blocks on stream I/O.
     */
    void OfferGameEvent(const char *eventName, const char *jsonPayload);
};
