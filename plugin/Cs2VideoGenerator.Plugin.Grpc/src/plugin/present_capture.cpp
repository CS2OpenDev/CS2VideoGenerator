/**
 * @file present_capture.cpp
 * @brief Streams raw backbuffer frames to the CSVG host while a startmovie recording runs.
 *
 * The transport behind the InEngineHooked capture provider. A vtable hook on
 * IDXGISwapChain::Present (and Present1) copies each presented backbuffer into a small
 * ring of staging textures and maps each copy two presents late, so the GPU->CPU
 * transfer overlaps rendering instead of stalling it (measured: the delayed map costs
 * ~0.001 ms against the ~8 ms synchronous readback startmovie's own frame dump pays).
 * Mapped frames are written to the named pipe \\.\pipe\csvg-frames as pitch-stripped
 * RGBA rows.
 *
 * Arming rides the plugin's own engine-thread command execution
 * (Cs2PluginClient::DrainEngineCommandQueue calls CsvgPresentCaptureOnEngineCommand for
 * every command): "startmovie" arms - but only if the CSVG host is listening on the
 * frame pipe, which is what keeps this module inert for every other capture provider -
 * and "endmovie" drains the ring, writes the end-of-stream record, and closes the pipe.
 *
 * Wire protocol (little-endian, 16-byte records, mirrored by the host's
 * PresentHookFrameConsumer): one CFRS {width, height, dxgiFormat} stream header, then
 * per frame CFRM {sequence, payloadBytes, 0} + payload, then CFRE {frameCount, 0, 0}.
 *
 * Boundary semantics (measured against real CS2): presents map 1:1 onto movie frames, and
 * exactly one present occurs between the startmovie command executing and the first
 * movie frame - so the first armed present is skipped. The trailing movie frame's
 * present lands after endmovie executes and is not captured; the stream is one frame
 * (1/fps) shorter than the wav, inside the host's audio-duration tolerance.
 *
 * Everything here must fail soft: a capture problem disarms and logs, it never takes
 * the game down.
 */

#ifdef _WIN32

#include <windows.h>

#include <d3d11.h>
#include <dxgi1_2.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

// server_plugin.cpp
void PatchVTableEntry(void **vtable, int index, void *newFunc);
void Log(const char *msg, ...);

namespace
{
constexpr const char *kPipePath = "\\\\.\\pipe\\csvg-frames";
constexpr int kPresentSlot = 8;   // IDXGISwapChain::Present
constexpr int kPresent1Slot = 22; // IDXGISwapChain1::Present1
constexpr int kRingDepth = 3;     // map lag = kRingDepth - 1 presents

constexpr uint32_t kStreamHeaderMagic = 0x53524643; // "CFRS"
constexpr uint32_t kFrameMagic = 0x4D524643;        // "CFRM"
constexpr uint32_t kTrailerMagic = 0x45524643;      // "CFRE"

typedef HRESULT(STDMETHODCALLTYPE *PresentFn)(IDXGISwapChain *self, UINT syncInterval, UINT flags);
typedef HRESULT(STDMETHODCALLTYPE *Present1Fn)(IDXGISwapChain1 *self, UINT syncInterval, UINT flags,
                                               const DXGI_PRESENT_PARAMETERS *params);

PresentFn g_originalPresent = nullptr;
Present1Fn g_originalPresent1 = nullptr;

/** Hook install state: attempted-once latch plus success flag. */
std::atomic_bool g_installAttempted{false};
std::atomic_bool g_hookInstalled{false};

/** Fast pre-check so an unarmed present costs one relaxed load and no lock. */
std::atomic_bool g_armedFast{false};

/**
 * Guards everything below: arm/disarm transitions, the D3D immediate context (NOT
 * thread-safe between the render thread's captures and the engine thread's endmovie
 * drain), the staging ring and the pipe handle.
 */
std::mutex g_mutex;

bool g_armed = false;
bool g_skipNext = false; // the one present between startmovie executing and movie frame 0
bool g_headerWritten = false;
HANDLE g_pipe = INVALID_HANDLE_VALUE;
uint32_t g_nextSequence = 0; // next frame sequence to send (== frames sent)
long long g_copied = 0;      // backbuffer copies made this clip (ring writes)
bool g_failedThisClip = false;

ID3D11Device *g_device = nullptr;
ID3D11DeviceContext *g_context = nullptr;
ID3D11Texture2D *g_ring[kRingDepth] = {};
UINT g_width = 0;
UINT g_height = 0;
DXGI_FORMAT g_format = DXGI_FORMAT_UNKNOWN;
std::vector<uint8_t> g_rowBuffer; // pitch-stripping fallback when RowPitch != width*4

struct FrameRecord
{
    uint32_t magic;
    uint32_t a;
    uint32_t b;
    uint32_t c;
};

void ReleaseRing()
{
    for (ID3D11Texture2D *&t : g_ring)
    {
        if (t != nullptr)
        {
            t->Release();
            t = nullptr;
        }
    }
}

void ClosePipeLocked()
{
    if (g_pipe != INVALID_HANDLE_VALUE)
    {
        FlushFileBuffers(g_pipe);
        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }
}

/** Disarms after a capture-side problem. The game keeps running; the host's pipe read fails loudly. */
void FailCaptureLocked(const char *what)
{
    if (!g_failedThisClip)
    {
        g_failedThisClip = true;
        Log("present-capture FAILED (%s) - disarming; the host will fail the clip", what);
    }

    ClosePipeLocked();
    g_armed = false;
    g_armedFast.store(false, std::memory_order_relaxed);
}

bool WriteAllLocked(const void *data, size_t bytes)
{
    const uint8_t *p = (const uint8_t *)data;
    while (bytes > 0)
    {
        DWORD written = 0;
        DWORD chunk = bytes > 0x40000000 ? 0x40000000 : (DWORD)bytes;
        if (!WriteFile(g_pipe, p, chunk, &written, nullptr) || written == 0)
        {
            return false;
        }
        p += written;
        bytes -= written;
    }
    return true;
}

bool WriteRecordLocked(uint32_t magic, uint32_t a, uint32_t b, uint32_t c)
{
    FrameRecord record{magic, a, b, c};
    return WriteAllLocked(&record, sizeof(record));
}

/**
 * Maps one ring slot and sends it as the next frame. The slot's copy is at least
 * (kRingDepth - 1) presents old on the steady-state path, so the map does not stall;
 * the endmovie drain maps fresher copies once, when there is nothing left to stall.
 */
bool MapAndSendLocked(long long copyIndex)
{
    ID3D11Texture2D *texture = g_ring[copyIndex % kRingDepth];
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    if (FAILED(g_context->Map(texture, 0, D3D11_MAP_READ, 0, &mapped)))
    {
        FailCaptureLocked("Map on a staging texture failed");
        return false;
    }

    const uint32_t rowBytes = g_width * 4;
    const uint32_t payload = rowBytes * g_height;
    bool ok = WriteRecordLocked(kFrameMagic, g_nextSequence, payload, 0);

    if (ok)
    {
        if (mapped.RowPitch == rowBytes)
        {
            // Tight rows (the common case at 1080p): stream the mapping directly.
            ok = WriteAllLocked(mapped.pData, (size_t)payload);
        }
        else
        {
            // Padded rows: consolidate once into a reusable buffer, then one write.
            if (g_rowBuffer.size() < payload)
            {
                g_rowBuffer.resize(payload);
            }
            const uint8_t *src = (const uint8_t *)mapped.pData;
            for (UINT row = 0; row < g_height; row++)
            {
                memcpy(g_rowBuffer.data() + (size_t)row * rowBytes, src + (size_t)row * mapped.RowPitch, rowBytes);
            }
            ok = WriteAllLocked(g_rowBuffer.data(), (size_t)payload);
        }
    }

    g_context->Unmap(texture, 0);

    if (!ok)
    {
        FailCaptureLocked("writing a frame to the pipe failed");
        return false;
    }

    g_nextSequence++;
    return true;
}

/** One armed present: copy the backbuffer into the ring, send the (depth-1)-old slot. */
void CapturePresentLocked(IDXGISwapChain *swapchain)
{
    if (g_skipNext)
    {
        g_skipNext = false;
        return;
    }

    ID3D11Texture2D *backbuffer = nullptr;
    if (FAILED(swapchain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void **)&backbuffer)) || backbuffer == nullptr)
    {
        FailCaptureLocked("GetBuffer(0) failed");
        return;
    }

    D3D11_TEXTURE2D_DESC desc = {};
    backbuffer->GetDesc(&desc);

    if (g_device == nullptr)
    {
        swapchain->GetDevice(__uuidof(ID3D11Device), (void **)&g_device);
        if (g_device != nullptr)
        {
            g_device->GetImmediateContext(&g_context);
        }
        if (g_device == nullptr || g_context == nullptr)
        {
            backbuffer->Release();
            FailCaptureLocked("could not reach the D3D11 device behind the swapchain");
            return;
        }
    }

    if (desc.SampleDesc.Count > 1)
    {
        backbuffer->Release();
        FailCaptureLocked("the backbuffer is multisampled, which this capture does not resolve");
        return;
    }

    if (g_ring[0] == nullptr || g_width != desc.Width || g_height != desc.Height || g_format != desc.Format)
    {
        if (g_headerWritten)
        {
            // Geometry changed mid-clip - the raw stream's shape is fixed at the header.
            backbuffer->Release();
            FailCaptureLocked("the backbuffer changed shape mid-clip");
            return;
        }

        ReleaseRing();
        D3D11_TEXTURE2D_DESC staging = desc;
        staging.Usage = D3D11_USAGE_STAGING;
        staging.BindFlags = 0;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        staging.MiscFlags = 0;
        for (ID3D11Texture2D *&t : g_ring)
        {
            if (FAILED(g_device->CreateTexture2D(&staging, nullptr, &t)))
            {
                ReleaseRing();
                backbuffer->Release();
                FailCaptureLocked("creating a staging texture failed");
                return;
            }
        }
        g_width = desc.Width;
        g_height = desc.Height;
        g_format = desc.Format;
    }

    if (!g_headerWritten)
    {
        if (!WriteRecordLocked(kStreamHeaderMagic, g_width, g_height, (uint32_t)g_format))
        {
            backbuffer->Release();
            FailCaptureLocked("writing the stream header failed");
            return;
        }
        g_headerWritten = true;
        Log("present-capture streaming %ux%u (dxgi format %d)", g_width, g_height, (int)g_format);
    }

    g_context->CopyResource(g_ring[g_copied % kRingDepth], backbuffer);
    backbuffer->Release();
    g_copied++;

    if (g_copied > kRingDepth - 1)
    {
        MapAndSendLocked(g_copied - kRingDepth);
    }
}

void OnPresent(IDXGISwapChain *swapchain)
{
    if (!g_armedFast.load(std::memory_order_relaxed))
    {
        return;
    }

    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_armed)
    {
        CapturePresentLocked(swapchain);
    }
}

HRESULT STDMETHODCALLTYPE HookedPresent(IDXGISwapChain *self, UINT syncInterval, UINT flags)
{
    OnPresent(self);
    return g_originalPresent(self, syncInterval, flags);
}

HRESULT STDMETHODCALLTYPE HookedPresent1(IDXGISwapChain1 *self, UINT syncInterval, UINT flags,
                                         const DXGI_PRESENT_PARAMETERS *params)
{
    OnPresent(self);
    return g_originalPresent1(self, syncInterval, flags, params);
}
} // namespace

/**
 * @brief Whether this plugin build can stream present-hook frames; decides whether the
 *        "present-capture" capability is advertised.
 */
bool CsvgPresentCaptureSupported()
{
    return true;
}

/**
 * @brief Installs the DXGI Present/Present1 vtable hook, once. Called every engine frame
 *        from the FrameStageNotify hook; one relaxed load after the first call.
 *
 * Technique (validated against real CS2): a throwaway D3D11 device + swapchain against
 * a hidden window shares dxgi.dll's swapchain vtable with the game's real swapchain, so
 * patching Present and Present1 there hooks the game. Coexists with the Steam overlay's
 * own Present hook (observed). Install failure is latched and logged - capture arms will
 * then refuse, and every other capture provider is unaffected.
 */
void CsvgPresentCaptureEnsureHook()
{
    if (g_installAttempted.load(std::memory_order_acquire))
    {
        return;
    }
    if (g_installAttempted.exchange(true))
    {
        return;
    }

    HWND wnd = CreateWindowExA(0, "STATIC", "csvg_present_capture", WS_OVERLAPPED, 0, 0, 16, 16, nullptr, nullptr,
                               nullptr, nullptr);
    if (wnd == nullptr)
    {
        Log("present-capture: CreateWindowExA failed (%lu) - hook NOT installed; InEngineHooked will not work",
            GetLastError());
        return;
    }

    DXGI_SWAP_CHAIN_DESC scd = {};
    scd.BufferCount = 2;
    scd.BufferDesc.Width = 16;
    scd.BufferDesc.Height = 16;
    scd.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scd.OutputWindow = wnd;
    scd.SampleDesc.Count = 1;
    scd.Windowed = TRUE;
    scd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    IDXGISwapChain *swapchain = nullptr;
    ID3D11Device *device = nullptr;
    ID3D11DeviceContext *context = nullptr;
    D3D_FEATURE_LEVEL level;
    HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
                                               D3D11_SDK_VERSION, &scd, &swapchain, &device, &level, &context);
    if (FAILED(hr) || swapchain == nullptr)
    {
        Log("present-capture: D3D11CreateDeviceAndSwapChain failed (0x%08lx) - hook NOT installed; "
            "InEngineHooked will not work",
            (unsigned long)hr);
        DestroyWindow(wnd);
        return;
    }

    void **vtable = *(void ***)swapchain;
    g_originalPresent = (PresentFn)vtable[kPresentSlot];
    g_originalPresent1 = (Present1Fn)vtable[kPresent1Slot];
    PatchVTableEntry(vtable, kPresentSlot, (void *)&HookedPresent);
    PatchVTableEntry(vtable, kPresent1Slot, (void *)&HookedPresent1);

    swapchain->Release();
    context->Release();
    device->Release();
    DestroyWindow(wnd);

    g_hookInstalled.store(true, std::memory_order_release);
    Log("present-capture: hooked IDXGISwapChain::Present (slot %d) and Present1 (slot %d)", kPresentSlot,
        kPresent1Slot);
}

/**
 * @brief Engine-thread seam: watches the commands the plugin executes and brackets the
 *        capture window on startmovie/endmovie.
 *
 * On "startmovie" the module tries to connect to the CSVG frame pipe; no listener means
 * no hooked capture was requested (the host only opens the pipe for InEngineHooked
 * clips) and the command passes untouched. On "endmovie" the ring's tail is drained and
 * the stream is closed - safe against the render thread because both sides hold
 * @ref g_mutex around every use of the immediate context.
 */
void CsvgPresentCaptureOnEngineCommand(const char *cmd)
{
    if (cmd == nullptr)
    {
        return;
    }

    if (strncmp(cmd, "startmovie", 10) == 0)
    {
        std::lock_guard<std::mutex> lock(g_mutex);

        if (g_armed)
        {
            Log("present-capture: startmovie while already armed - ignoring the second arm");
            return;
        }

        HANDLE pipe = CreateFileA(kPipePath, GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            // Nobody is listening: not a hooked capture. Stay silent - this is the normal
            // path for the file-based provider and for user-typed startmovie.
            return;
        }

        if (!g_hookInstalled.load(std::memory_order_acquire))
        {
            CloseHandle(pipe);
            Log("present-capture: the host is listening on the frame pipe but the Present hook is not "
                "installed - the clip will fail on the host side");
            return;
        }

        g_pipe = pipe;
        g_headerWritten = false;
        g_skipNext = true;
        g_nextSequence = 0;
        g_copied = 0;
        g_failedThisClip = false;
        g_armed = true;
        g_armedFast.store(true, std::memory_order_relaxed);
        Log("present-capture: armed at '%s'", cmd);
    }
    else if (strncmp(cmd, "endmovie", 8) == 0)
    {
        std::lock_guard<std::mutex> lock(g_mutex);

        if (!g_armed)
        {
            return;
        }

        // Drain what the delayed map still owes: every copy made but not yet sent
        // (sequence numbers equal copy indexes, so the pending window is exactly
        // [nextSequence, copied)). These maps are fresher than the steady-state lag,
        // but there is nothing left to overlap with - the recording is over and one
        // stalled map costs nothing anyone is waiting on.
        for (long long i = g_nextSequence; g_armed && i < g_copied; i++)
        {
            MapAndSendLocked(i);
        }

        if (g_armed)
        {
            WriteRecordLocked(kTrailerMagic, g_nextSequence, 0, 0);
            Log("present-capture: disarmed after %u frame(s)", g_nextSequence);
            ClosePipeLocked();
            g_armed = false;
            g_armedFast.store(false, std::memory_order_relaxed);
        }
    }
}

#else // !_WIN32

bool CsvgPresentCaptureSupported()
{
    return false;
}

void CsvgPresentCaptureEnsureHook()
{
}

void CsvgPresentCaptureOnEngineCommand(const char *)
{
}

#endif
