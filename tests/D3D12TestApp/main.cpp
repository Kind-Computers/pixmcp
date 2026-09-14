// Minimal D3D12 test application used to exercise pixmcp end to end. Each frame renders three
// triangles (one indexed, one textured through a descriptor table) with nested PIX markers on the
// graphics queue and runs a small compute dispatch on a separate compute queue, so a GPU capture has
// draws, a dispatch, two queues, a PSO, a root signature with a descriptor table, vertex/index/
// constant buffers, a texture SRV, a UAV and per-event timing.
//
// Usage: D3D12TestApp.exe [--frames N] [--variant baseline|candidate] [--adapter-name TEXT] [--duplicate-markers] [--hidden] [--hang [--hang-after N]]
//   --frames N      stop after N frames (default: run until the window is closed)
//   --variant       deterministic baseline (default) or candidate: altered shader, an extra pass,
//                   larger compute resource, and distinct root constants; marker paths remain stable.
//   --duplicate-markers  emit a second "Triangle pass" to test ambiguous marker matching
//   --hidden        keep the test window hidden for automated capture runs
//   --adapter-name  case-insensitive hardware-adapter name substring; no fallback on a missing match.
//                   Omit for the first usable high-performance adapter. Startup logs the selected
//                   adapter name, vendor/device IDs and LUID to stdout and flushes before rendering.
//   --startup-delay-ms N  wait before creating the D3D12 device (readiness regression fixture)
//   --timing-workload     emit named CPU PIX events/counter and spend CPU time in known noinline functions
//   --hang          submit a never-terminating compute shader after --hang-after frames (default 30)
//                   to provoke a GPU timeout / device removal, which produces a DirectX dump file when
//                   DRED and dump-file retention are enabled (see pix_device_d3d_settings_set).
// Rich fixture flags compose; an unsupported feature prints "fixture: skipped <flag>: <reason>" and rendering continues:
//   --depth         D32 depth buffer and a "Depth pass" whose second triangle fails the depth test
//   --placed-heap   8 MiB heap with a placed buffer and texture; "Placed pass" copies into the buffer
//   --reserved      4096x4096 reserved texture with 4 mapped tiles; "Reserved pass" copies into them
//   --indirect      command signature and a 4-entry argument buffer drawn with ExecuteIndirect ("Indirect pass")
//   --async-overlap 4096-iteration compute; the graphics queue waits on the compute fence ("Consume compute")
//   --msaa N        render into an N-sample target resolved to the swap chain ("Resolve pass")
//   --mrt 2         add an R16G16B16A16_FLOAT second render target
//   --bandwidth     2048x2048 float texture sampled 8 times per pixel ("Bandwidth pass")
//   --hdr           R16G16B16A16_FLOAT swap chain with the linear sRGB colour space when supported
//   --gpu-markers   command-list markers through WinPixEventRuntime so timing captures record GPU markers
//   --dxc           compile with dxcompiler.dll at shader model 6.0 with embedded debug information
//   --mesh          (implies --dxc) mesh-shader quad in "Mesh pass"
//   --programmatic-capture PATH  PIXGpuCaptureNextFrames(PATH, --capture-frames N) at frame --capture-at N (defaults 30, 1)
//   --workload perf 1920x1080 offscreen frame (Shadow, GBuffer, Lighting, Post) with depth, indirect draws, async
//                   compute overlap, a copy-queue upload and GPU markers; the candidate doubles the Lighting cost
//   --report PATH   write the effective flags, skips, adapter and DXC version as JSON
// Build:  build.cmd  (requires Visual Studio 2022 C++ tools and the Windows 10/11 SDK)

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>
#include <pix3.h>
#include <dxcapi.h>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "dxcompiler.lib") // delay-loaded by build.cmd, so only --dxc needs dxcompiler.dll

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr UINT kFrameCount = 2;
    constexpr UINT kWidth = 640;
    constexpr UINT kHeight = 480;
    constexpr UINT kTextureSize = 4;
    constexpr UINT kComputeElements = 256;

    struct Vertex { float position[3]; float color[4]; float uv[2]; };
    struct Constants { float angle; float aspect; float pad[62]; }; // 256-byte aligned CBV

    ComPtr<ID3D12Device> g_device;
    ComPtr<ID3D12CommandQueue> g_queue;
    ComPtr<ID3D12CommandQueue> g_computeQueue;
    ComPtr<IDXGISwapChain3> g_swapChain;
    ComPtr<ID3D12DescriptorHeap> g_rtvHeap;
    ComPtr<ID3D12DescriptorHeap> g_srvHeap;
    ComPtr<ID3D12Resource> g_renderTargets[kFrameCount];
    ComPtr<ID3D12CommandAllocator> g_allocators[kFrameCount];
    ComPtr<ID3D12CommandAllocator> g_computeAllocator;
    ComPtr<ID3D12GraphicsCommandList> g_commandList;
    ComPtr<ID3D12GraphicsCommandList> g_computeList;
    ComPtr<ID3D12RootSignature> g_rootSignature;
    ComPtr<ID3D12RootSignature> g_computeRootSignature;
    ComPtr<ID3D12PipelineState> g_pipelineState;
    ComPtr<ID3D12PipelineState> g_computeState;
    ComPtr<ID3D12PipelineState> g_hangState;
    ComPtr<ID3D12Resource> g_vertexBuffer;
    ComPtr<ID3D12Resource> g_indexBuffer;
    ComPtr<ID3D12Resource> g_constantBuffer;
    ComPtr<ID3D12Resource> g_texture;
    ComPtr<ID3D12Resource> g_textureUpload;
    ComPtr<ID3D12Resource> g_computeOutput;
    // Rich render configuration: the swap chain format, an MSAA scene target, a second render target and depth.
    DXGI_FORMAT g_backBufferFormat = DXGI_FORMAT_R8G8B8A8_UNORM;
    constexpr DXGI_FORMAT kExtraTargetFormat = DXGI_FORMAT_R16G16B16A16_FLOAT;
    ComPtr<ID3D12Resource> g_sceneColor;
    ComPtr<ID3D12Resource> g_sceneExtra;
    ComPtr<ID3D12Resource> g_depthBuffer;
    ComPtr<ID3D12DescriptorHeap> g_dsvHeap;
    ComPtr<ID3D12Resource> g_depthVertexBuffer;
    D3D12_VERTEX_BUFFER_VIEW g_depthVbv = {};
    // Rich resources and passes.
    ComPtr<ID3D12Heap> g_placedHeap;
    ComPtr<ID3D12Resource> g_placedBuffer;
    ComPtr<ID3D12Resource> g_placedTexture;
    ComPtr<ID3D12Heap> g_tileHeap;
    ComPtr<ID3D12Resource> g_reservedTexture;
    ComPtr<ID3D12CommandSignature> g_drawSignature;
    ComPtr<ID3D12Resource> g_indirectArguments;
    ComPtr<ID3D12PipelineState> g_heavyComputeState;
    ComPtr<ID3D12Resource> g_bandwidthTexture;
    ComPtr<ID3D12PipelineState> g_bandwidthState;
    ComPtr<ID3D12Resource> g_fullscreenVertexBuffer;
    D3D12_VERTEX_BUFFER_VIEW g_fullscreenVbv = {};
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT g_checkerFootprint = {};
    ComPtr<ID3D12RootSignature> g_meshRootSignature;
    // --workload perf: a 1080p offscreen frame, its pipelines and a copy queue.
    constexpr UINT kPerfWidth = 1920;
    constexpr UINT kPerfHeight = 1080;
    ComPtr<ID3D12Resource> g_perfColor;
    ComPtr<ID3D12Resource> g_perfDepth;
    ComPtr<ID3D12DescriptorHeap> g_perfRtvHeap;
    ComPtr<ID3D12DescriptorHeap> g_perfDsvHeap;
    ComPtr<ID3D12PipelineState> g_perfState;
    ComPtr<ID3D12PipelineState> g_lightingState;
    ComPtr<ID3D12PipelineState> g_postState;
    ComPtr<ID3D12CommandQueue> g_copyQueue;
    ComPtr<ID3D12CommandAllocator> g_copyAllocator;
    ComPtr<ID3D12GraphicsCommandList> g_copyList;
    ComPtr<ID3D12Resource> g_copyTarget;
    ComPtr<ID3D12Fence> g_copyFence;
    HANDLE g_copyFenceEvent = nullptr;
    UINT64 g_copyFenceValue = 0;
    ComPtr<ID3D12PipelineState> g_meshState;

    // One pipeline state stream subobject: the type tag and its value, pointer-aligned as the runtime expects.
    template <typename T, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE Type>
    struct alignas(void*) StreamItem
    {
        D3D12_PIPELINE_STATE_SUBOBJECT_TYPE type = Type;
        T value{};
    };
    ComPtr<ID3D12Fence> g_fence;
    ComPtr<ID3D12Fence> g_computeFence;
    HANDLE g_fenceEvent = nullptr;
    HANDLE g_computeFenceEvent = nullptr;
    UINT64 g_fenceValues[kFrameCount] = {};
    UINT64 g_computeFenceValue = 0;
    UINT g_frameIndex = 0;
    UINT g_rtvStride = 0;
    D3D12_VERTEX_BUFFER_VIEW g_vbv = {};
    D3D12_INDEX_BUFFER_VIEW g_ibv = {};
    Constants* g_mappedConstants = nullptr;
    bool g_quit = false;
    bool g_candidate = false;
    bool g_duplicateMarkers = false;
    std::wstring g_adapterName;
    volatile double g_timingSink = 0;

    struct FixtureOptions
    {
        bool depth = false, placedHeap = false, reserved = false, indirect = false, asyncOverlap = false, bandwidth = false;
        bool hdr = false, gpuMarkers = false, dxc = false, mesh = false, perf = false;
        UINT msaa = 1, mrt = 1;
        std::wstring programmaticCapture, reportPath;
        int captureAt = 30, captureFrames = 1;
    };
    FixtureOptions g_options;
    std::vector<std::string> g_flags, g_skips;
    std::string g_adapterDescription, g_dxcVersion, g_captureResult;
    UINT g_adapterVendorId = 0;

    std::string Utf8(const std::wstring& text)
    {
        if (text.empty()) return {};
        int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string result(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
        return result;
    }

    // Records an unsupported feature, turns it off and keeps rendering.
    void SkipFeature(const char* flag, const std::string& reason)
    {
        std::printf("fixture: skipped %s: %s\n", flag, reason.c_str());
        g_skips.push_back(std::string(flag) + ": " + reason);
    }

    std::string JsonString(const std::string& text)
    {
        std::string out = "\"";
        for (char c : text)
        {
            if (c == '"' || c == '\\') { out += '\\'; out += c; }
            else if (static_cast<unsigned char>(c) < 0x20) { char code[8]; std::snprintf(code, sizeof(code), "\\u%04x", c); out += code; }
            else out += c;
        }
        return out + "\"";
    }

    std::string JsonList(const std::vector<std::string>& items)
    {
        std::string out = "[";
        for (size_t i = 0; i < items.size(); ++i) out += (i ? ", " : "") + JsonString(items[i]);
        return out + "]";
    }

    void WriteReport()
    {
        if (g_options.reportPath.empty()) return;
        FILE* file = nullptr;
        if (_wfopen_s(&file, g_options.reportPath.c_str(), L"wb") != 0 || !file) { std::fwprintf(stderr, L"Cannot write --report %ls\n", g_options.reportPath.c_str()); return; }
        std::fprintf(file, "{\n  \"variant\": %s,\n  \"flags\": %s,\n  \"skips\": %s,\n  \"adapter\": %s,\n  \"vendorId\": %u,\n  \"dxcVersion\": %s,\n  \"programmaticCapture\": %s\n}\n",
            JsonString(g_candidate ? "candidate" : "baseline").c_str(), JsonList(g_flags).c_str(), JsonList(g_skips).c_str(),
            JsonString(g_adapterDescription).c_str(), g_adapterVendorId, g_dxcVersion.empty() ? "null" : JsonString(g_dxcVersion).c_str(),
            g_captureResult.empty() ? "null" : JsonString(g_captureResult).c_str());
        std::fclose(file);
    }

    __declspec(noinline) void FixtureTimingInner()
    {
        LARGE_INTEGER frequency, started, now;
        QueryPerformanceFrequency(&frequency);
        QueryPerformanceCounter(&started);
        double value = g_timingSink;
        do
        {
            for (int i = 1; i <= 2000; ++i) value = std::sin(value + i * 0.00001);
            QueryPerformanceCounter(&now);
        } while (now.QuadPart - started.QuadPart < frequency.QuadPart / 125); // about 8 ms of sampled CPU work
        g_timingSink = value;
    }

    __declspec(noinline) void FixtureTimingMiddle()
    {
        FixtureTimingInner();
        g_timingSink += 0.00001; // keep this frame visible; prevent a tail-call
    }

    __declspec(noinline) void FixtureTimingOuter()
    {
        PIXBeginEvent(PIX_COLOR(20, 180, 80), L"Fixture CPU Work");
        FixtureTimingMiddle();
        PIXSetMarker(PIX_COLOR(220, 180, 20), L"Fixture CPU Marker");
        PIXEndEvent();
    }

    void Check(HRESULT hr, const char* what)
    {
        if (FAILED(hr))
        {
            std::fprintf(stderr, "%s failed: 0x%08X\n", what, static_cast<unsigned>(hr));
            if (g_device && (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_HUNG || hr == DXGI_ERROR_DEVICE_RESET))
            {
                std::fprintf(stderr, "Device removed reason: 0x%08X\n", static_cast<unsigned>(g_device->GetDeviceRemovedReason()));
            }
            std::exit(1);
        }
    }

    // PIX markers via the D3D12 command list metadata API (no WinPixEventRuntime dependency).
    // PIX_EVENT_UNICODE_VERSION == 0, payload is a null-terminated wide string.
    // With --gpu-markers the WinPixEventRuntime encoding is used, which timing captures record as GPU markers.
    void BeginEvent(ID3D12GraphicsCommandList* list, const wchar_t* name)
    {
        if (g_options.gpuMarkers) { PIXBeginEvent(list, PIX_COLOR(64, 128, 220), name); return; }
        list->BeginEvent(0, name, static_cast<UINT>((std::wcslen(name) + 1) * sizeof(wchar_t)));
    }
    void EndEvent(ID3D12GraphicsCommandList* list)
    {
        if (g_options.gpuMarkers) { PIXEndEvent(list); return; }
        list->EndEvent();
    }
    void SetMarker(ID3D12GraphicsCommandList* list, const wchar_t* name)
    {
        if (g_options.gpuMarkers) { PIXSetMarker(list, PIX_COLOR(220, 180, 20), name); return; }
        list->SetMarker(0, name, static_cast<UINT>((std::wcslen(name) + 1) * sizeof(wchar_t)));
    }

    void SetName(ID3D12Object* object, const wchar_t* name) { object->SetName(name); }

    D3D12_CPU_DESCRIPTOR_HANDLE RtvHandle(UINT index)
    {
        D3D12_CPU_DESCRIPTOR_HANDLE handle = g_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        handle.ptr += static_cast<SIZE_T>(index) * g_rtvStride;
        return handle;
    }

    void Transition(ID3D12GraphicsCommandList* list, ID3D12Resource* resource, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
    {
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = resource;
        barrier.Transition.StateBefore = from;
        barrier.Transition.StateAfter = to;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        list->ResourceBarrier(1, &barrier);
    }

    ComPtr<ID3D12Resource> CreateTarget(DXGI_FORMAT format, UINT width, UINT height, UINT samples, D3D12_RESOURCE_FLAGS flags,
        D3D12_RESOURCE_STATES state, const D3D12_CLEAR_VALUE* clear, const wchar_t* name)
    {
        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = width;
        desc.Height = height;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = format;
        desc.SampleDesc.Count = samples;
        desc.Flags = flags;
        ComPtr<ID3D12Resource> target;
        Check(g_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, state, clear, IID_PPV_ARGS(&target)), "CreateCommittedResource (target)");
        SetName(target.Get(), name);
        return target;
    }

    const char* kShader = R"(
cbuffer Constants : register(b0) { float angle; float aspect; };
cbuffer DrawConstants : register(b1) { uint materialId; uint variantId; uint flags; float exposure; };
Texture2D Pattern : register(t0);
SamplerState PatternSampler : register(s0);
struct VSIn  { float3 pos : POSITION; float4 color : COLOR; float2 uv : TEXCOORD; };
struct PSIn  { float4 pos : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD; };
PSIn VSMain(VSIn input)
{
    float c = cos(angle), s = sin(angle);
    float2 p = float2(input.pos.x * c - input.pos.y * s, input.pos.x * s + input.pos.y * c);
    PSIn o;
    o.pos = float4(p.x / aspect, p.y, input.pos.z, 1.0);
    o.color = input.color;
    o.uv = input.uv;
    return o;
}
float4 PSMain(PSIn input) : SV_TARGET
{
    float4 color = input.color * Pattern.Sample(PatternSampler, input.uv);
    float enabled = materialId == 0x50495831 && (flags & 1) != 0 && variantId != 0 ? 1.0 : 0.0;
    return float4(color.rgb * exposure * enabled, color.a);
}
float4 PSMainCandidate(PSIn input) : SV_TARGET
{
    float4 color = PSMain(input);
    return float4(color.bgr * float3(0.7, 1.0, 0.8), color.a);
}
)";

    // Rich-only pixel shaders, compiled from kShader plus this text so the default shader source stays byte-identical.
    const char* kRichShaderExtra = R"(
struct MrtOut { float4 color : SV_Target0; float4 extra : SV_Target1; };
float4 PSBandwidth(PSIn input) : SV_TARGET
{
    // Eight scattered taps per pixel of a 2048x2048 float texture: memory traffic rather than arithmetic.
    float4 sum = 0;
    for (int i = 0; i < 8; i++) sum += Pattern.Sample(PatternSampler, input.uv * (1.0 + i * 0.37) + float2(i * 0.11, i * 0.07));
    return float4(sum.rgb / 8.0 + input.color.rgb * 0.25, 1.0);
}
float4 PSLighting(PSIn input) : SV_TARGET
{
    // variantId is 1 for the baseline and 2 for the candidate, so the candidate doubles the shading work.
    uint iterations = 96 * max(variantId, 1);
    float3 light = 0;
    [loop] for (uint i = 0; i < iterations; i++)
    {
        float t = i * 0.0625;
        light += saturate(sin(input.uv.x * 40.0 + t) * cos(input.uv.y * 30.0 - t)) * float3(0.004, 0.003, 0.002);
    }
    return float4(input.color.rgb * 0.2 + light, 1.0);
}
MrtOut PSMainMrt(PSIn input)
{
    MrtOut o;
    o.color = PSMain(input);
    o.extra = float4(input.uv, input.pos.z, 1.0);
    return o;
}
)";

    const char* kMeshShader = R"(
struct MeshVertex { float4 pos : SV_Position; float4 color : COLOR; };
[outputtopology("triangle")]
[numthreads(1, 1, 1)]
void MSMain(out indices uint3 triangles[2], out vertices MeshVertex quad[4])
{
    SetMeshOutputCounts(4, 2);
    float2 corners[4] = { float2(-0.95, -0.55), float2(-0.55, -0.55), float2(-0.95, -0.95), float2(-0.55, -0.95) };
    for (uint i = 0; i < 4; i++)
    {
        quad[i].pos = float4(corners[i], 0.1, 1.0);
        quad[i].color = float4(0.3 * i, 0.8, 1.0 - 0.2 * i, 1.0);
    }
    triangles[0] = uint3(0, 1, 2);
    triangles[1] = uint3(2, 1, 3);
}
float4 PSMesh(MeshVertex input) : SV_Target { return input.color; }
)";

    const char* kHeavyComputeShader = R"(
RWStructuredBuffer<float> Output : register(u0);
cbuffer ComputeConstants : register(b0) { float angle; };
[numthreads(64, 1, 1)]
void CSHeavy(uint3 id : SV_DispatchThreadID)
{
    float value = angle;
    for (uint i = 0; i < 4096; i++) value = sin(value + id.x * 0.01 + i);
    Output[id.x] = value;
}
)";

    const char* kComputeShader = R"(
RWStructuredBuffer<float> Output : register(u0);
cbuffer ComputeConstants : register(b0) { float angle; };
[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    Output[id.x] = sin(angle + id.x * 0.01);
}
// Never terminates: every iteration has a visible side effect, so the compiler keeps the loop.
[numthreads(64, 1, 1)]
void CSHang(uint3 id : SV_DispatchThreadID)
{
    float v = angle;
    while (v >= -1000000.0)
    {
        v = Output[id.x] + 1.0;
        Output[id.x] = v;
    }
}
)";

    LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WM_DESTROY) { g_quit = true; PostQuitMessage(0); return 0; }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    void WaitForGpu()
    {
        Check(g_queue->Signal(g_fence.Get(), g_fenceValues[g_frameIndex]), "Signal");
        Check(g_fence->SetEventOnCompletion(g_fenceValues[g_frameIndex], g_fenceEvent), "SetEventOnCompletion");
        WaitForSingleObjectEx(g_fenceEvent, INFINITE, FALSE);
        g_fenceValues[g_frameIndex]++;
    }

    void WaitForCompute()
    {
        if (g_computeFence->GetCompletedValue() < g_computeFenceValue)
        {
            Check(g_computeFence->SetEventOnCompletion(g_computeFenceValue, g_computeFenceEvent), "SetEventOnCompletion (compute)");
            WaitForSingleObjectEx(g_computeFenceEvent, INFINITE, FALSE);
        }
    }

    void MoveToNextFrame()
    {
        const UINT64 current = g_fenceValues[g_frameIndex];
        Check(g_queue->Signal(g_fence.Get(), current), "Signal");
        g_frameIndex = g_swapChain->GetCurrentBackBufferIndex();
        if (g_fence->GetCompletedValue() < g_fenceValues[g_frameIndex])
        {
            Check(g_fence->SetEventOnCompletion(g_fenceValues[g_frameIndex], g_fenceEvent), "SetEventOnCompletion");
            WaitForSingleObjectEx(g_fenceEvent, INFINITE, FALSE);
        }
        g_fenceValues[g_frameIndex] = current + 1;
    }

    ComPtr<ID3D12Resource> CreateBuffer(UINT64 size, D3D12_HEAP_TYPE heapType, D3D12_RESOURCE_STATES state, D3D12_RESOURCE_FLAGS flags, const wchar_t* name)
    {
        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = heapType;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = size;
        desc.Height = 1;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        desc.Flags = flags;
        ComPtr<ID3D12Resource> buffer;
        Check(g_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr, IID_PPV_ARGS(&buffer)), "CreateCommittedResource");
        SetName(buffer.Get(), name);
        return buffer;
    }

    ComPtr<ID3D12Resource> CreateUploadBuffer(UINT64 size, const wchar_t* name)
    {
        return CreateBuffer(size, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ, D3D12_RESOURCE_FLAG_NONE, name);
    }

    struct ShaderCode
    {
        std::vector<BYTE> bytes;
        D3D12_SHADER_BYTECODE View() const { return { bytes.data(), bytes.size() }; }
    };

    ShaderCode CompileDxc(const char* source, const char* entry, const char* target)
    {
        static ComPtr<IDxcCompiler3> compiler;
        if (!compiler)
        {
            Check(DxcCreateInstance(CLSID_DxcCompiler, IID_PPV_ARGS(&compiler)), "DxcCreateInstance (dxcompiler.dll and dxil.dll beside the executable)");
            ComPtr<IDxcVersionInfo> version;
            UINT32 major = 0, minor = 0;
            if (SUCCEEDED(compiler.As(&version)) && SUCCEEDED(version->GetVersion(&major, &minor)))
                g_dxcVersion = std::to_string(major) + "." + std::to_string(minor);
        }
        std::wstring wideEntry(entry, entry + std::strlen(entry)), wideTarget(target, target + std::strlen(target));
        LPCWSTR arguments[] = { L"-E", wideEntry.c_str(), L"-T", wideTarget.c_str(), L"-Zi", L"-Qembed_debug", L"-Od" };
        DxcBuffer buffer = { source, std::strlen(source), DXC_CP_UTF8 };
        ComPtr<IDxcResult> result;
        Check(compiler->Compile(&buffer, arguments, _countof(arguments), nullptr, IID_PPV_ARGS(&result)), "IDxcCompiler3::Compile");
        HRESULT status = E_FAIL;
        result->GetStatus(&status);
        if (FAILED(status))
        {
            ComPtr<IDxcBlobUtf8> errors;
            result->GetOutput(DXC_OUT_ERRORS, IID_PPV_ARGS(&errors), nullptr);
            std::fprintf(stderr, "%s compile (%s): %s\n", entry, target, errors && errors->GetStringLength() ? errors->GetStringPointer() : "?");
            std::exit(1);
        }
        ComPtr<IDxcBlob> object;
        Check(result->GetOutput(DXC_OUT_OBJECT, IID_PPV_ARGS(&object), nullptr), "DXC object output");
        const BYTE* data = static_cast<const BYTE*>(object->GetBufferPointer());
        return { std::vector<BYTE>(data, data + object->GetBufferSize()) };
    }

    // stage is vs, ps, cs, ms or psm (a mesh pipeline's pixel shader): fxc shader model 5.0 by default, DXC 6.0 with --dxc,
    // and 6.5 for the mesh pipeline.
    ShaderCode CompileShader(const char* source, const char* entry, const char* stage)
    {
        bool meshPipeline = std::strcmp(stage, "ms") == 0 || std::strcmp(stage, "psm") == 0;
        std::string target = std::string(std::strcmp(stage, "psm") == 0 ? "ps" : stage) + (meshPipeline ? "_6_5" : g_options.dxc ? "_6_0" : "_5_0");
        if (g_options.dxc) return CompileDxc(source, entry, target.c_str());
        ComPtr<ID3DBlob> blob, error;
        UINT compileFlags = D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
        if (FAILED(D3DCompile(source, std::strlen(source), "triangle.hlsl", nullptr, nullptr, entry, target.c_str(), compileFlags, 0, &blob, &error)))
        {
            std::fprintf(stderr, "%s compile: %s\n", entry, error ? static_cast<const char*>(error->GetBufferPointer()) : "?");
            std::exit(1);
        }
        const BYTE* data = static_cast<const BYTE*>(blob->GetBufferPointer());
        return { std::vector<BYTE>(data, data + blob->GetBufferSize()) };
    }

    // A 4x4 checkerboard texture, uploaded once through an upload buffer on the graphics queue.
    void CreateTexture(ID3D12GraphicsCommandList* list)
    {
        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = D3D12_HEAP_TYPE_DEFAULT;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = kTextureSize;
        desc.Height = kTextureSize;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        Check(g_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g_texture)), "CreateCommittedResource (texture)");
        SetName(g_texture.Get(), L"Checkerboard Texture");

        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint = {};
        UINT64 uploadSize = 0;
        g_device->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, nullptr, nullptr, &uploadSize);
        g_checkerFootprint = footprint;
        g_textureUpload = CreateUploadBuffer(uploadSize, L"Checkerboard Upload");
        BYTE* mapped = nullptr;
        D3D12_RANGE noRead = { 0, 0 };
        Check(g_textureUpload->Map(0, &noRead, reinterpret_cast<void**>(&mapped)), "Map texture upload");
        for (UINT y = 0; y < kTextureSize; ++y)
        {
            for (UINT x = 0; x < kTextureSize; ++x)
            {
                BYTE* pixel = mapped + footprint.Offset + y * footprint.Footprint.RowPitch + x * 4;
                BYTE v = ((x + y) & 1) ? 255 : 160;
                pixel[0] = v; pixel[1] = v; pixel[2] = v; pixel[3] = 255;
            }
        }
        g_textureUpload->Unmap(0, nullptr);

        D3D12_TEXTURE_COPY_LOCATION dst = {};
        dst.pResource = g_texture.Get();
        dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        D3D12_TEXTURE_COPY_LOCATION src = {};
        src.pResource = g_textureUpload.Get();
        src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        src.PlacedFootprint = footprint;
        list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = g_texture.Get();
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        list->ResourceBarrier(1, &barrier);

        D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
        srv.Format = desc.Format;
        srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        srv.Texture2D.MipLevels = 1;
        g_device->CreateShaderResourceView(g_texture.Get(), &srv, g_srvHeap->GetCPUDescriptorHandleForHeapStart());
    }

    void Init(HWND hwnd)
    {
        UINT factoryFlags = 0;
#if defined(_DEBUG)
        {
            ComPtr<ID3D12Debug> debug;
            if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)))) { debug->EnableDebugLayer(); factoryFlags |= DXGI_CREATE_FACTORY_DEBUG; }
        }
#endif
        ComPtr<IDXGIFactory6> factory;
        Check(CreateDXGIFactory2(factoryFlags, IID_PPV_ARGS(&factory)), "CreateDXGIFactory2");

        ComPtr<IDXGIAdapter1> adapter;
        if (!g_adapterName.empty())
            std::wprintf(L"Adapter request: %ls (case-insensitive substring)\n", g_adapterName.c_str());
        for (UINT i = 0; SUCCEEDED(factory->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter))); ++i)
        {
            DXGI_ADAPTER_DESC1 desc = {};
            Check(adapter->GetDesc1(&desc), "GetDesc1");
            if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            if (!g_adapterName.empty() && FindStringOrdinal(FIND_FROMSTART, desc.Description, -1,
                g_adapterName.c_str(), -1, TRUE) < 0) continue;
            if (SUCCEEDED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&g_device))))
            {
                std::wprintf(L"Adapter: %ls; vendor=0x%04X; device=0x%04X; LUID=%08X:%08X\n", desc.Description,
                    desc.VendorId, desc.DeviceId, static_cast<unsigned>(desc.AdapterLuid.HighPart),
                    static_cast<unsigned>(desc.AdapterLuid.LowPart));
                std::fflush(stdout);
                g_adapterDescription = Utf8(desc.Description);
                g_adapterVendorId = desc.VendorId;
                break;
            }
        }
        if (!g_device && !g_adapterName.empty())
        {
            std::fwprintf(stderr, L"No usable D3D12 hardware adapter matches --adapter-name '%ls'. No other adapter was selected.\n",
                g_adapterName.c_str());
            std::exit(1);
        }
        if (!g_device) Check(E_FAIL, "No D3D12 adapter");
        SetName(g_device.Get(), L"D3D12TestApp Device");
        if (g_options.hdr) g_backBufferFormat = DXGI_FORMAT_R16G16B16A16_FLOAT;
        if (g_options.msaa > 1)
        {
            D3D12_FEATURE_DATA_MULTISAMPLE_QUALITY_LEVELS levels = {};
            levels.Format = g_backBufferFormat;
            levels.SampleCount = g_options.msaa;
            if (FAILED(g_device->CheckFeatureSupport(D3D12_FEATURE_MULTISAMPLE_QUALITY_LEVELS, &levels, sizeof(levels))) || levels.NumQualityLevels == 0)
            {
                SkipFeature("--msaa", std::to_string(g_options.msaa) + " samples are not supported for the back buffer format");
                g_options.msaa = 1;
            }
        }

        D3D12_COMMAND_QUEUE_DESC queueDesc = {};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        Check(g_device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&g_queue)), "CreateCommandQueue");
        SetName(g_queue.Get(), L"Main Graphics Queue");
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
        Check(g_device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&g_computeQueue)), "CreateCommandQueue (compute)");
        SetName(g_computeQueue.Get(), L"Async Compute Queue");

        DXGI_SWAP_CHAIN_DESC1 scDesc = {};
        scDesc.BufferCount = kFrameCount;
        scDesc.Width = kWidth;
        scDesc.Height = kHeight;
        scDesc.Format = g_backBufferFormat;
        scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        scDesc.SampleDesc.Count = 1;
        ComPtr<IDXGISwapChain1> swapChain1;
        Check(factory->CreateSwapChainForHwnd(g_queue.Get(), hwnd, &scDesc, nullptr, nullptr, &swapChain1), "CreateSwapChainForHwnd");
        Check(swapChain1.As(&g_swapChain), "SwapChain QI");
        g_frameIndex = g_swapChain->GetCurrentBackBufferIndex();
        if (g_options.hdr)
        {
            UINT support = 0;
            if (SUCCEEDED(g_swapChain->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, &support))
                && (support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT))
                Check(g_swapChain->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709), "SetColorSpace1");
            else
                SkipFeature("--hdr colour space", "the output does not present linear sRGB; the float swap chain is kept");
        }

        D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc = {};
        rtvHeapDesc.NumDescriptors = kFrameCount + 2; // back buffers, then the MSAA scene target and the second render target
        rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        Check(g_device->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&g_rtvHeap)), "CreateDescriptorHeap");
        SetName(g_rtvHeap.Get(), L"RTV Heap");
        g_rtvStride = g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

        D3D12_DESCRIPTOR_HEAP_DESC srvHeapDesc = {};
        srvHeapDesc.NumDescriptors = g_options.bandwidth ? 2 : 1; // slot 1: the bandwidth texture
        srvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        srvHeapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        Check(g_device->CreateDescriptorHeap(&srvHeapDesc, IID_PPV_ARGS(&g_srvHeap)), "CreateDescriptorHeap (SRV)");
        SetName(g_srvHeap.Get(), L"SRV Heap");

        D3D12_CPU_DESCRIPTOR_HANDLE rtv = g_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < kFrameCount; ++i)
        {
            Check(g_swapChain->GetBuffer(i, IID_PPV_ARGS(&g_renderTargets[i])), "GetBuffer");
            g_device->CreateRenderTargetView(g_renderTargets[i].Get(), nullptr, rtv);
            SetName(g_renderTargets[i].Get(), i == 0 ? L"BackBuffer 0" : L"BackBuffer 1");
            rtv.ptr += g_rtvStride;
            Check(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g_allocators[i])), "CreateCommandAllocator");
        }
        Check(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, IID_PPV_ARGS(&g_computeAllocator)), "CreateCommandAllocator (compute)");

        if (g_options.msaa > 1)
        {
            D3D12_CLEAR_VALUE clear = { g_backBufferFormat, { 0.05f, 0.05f, 0.15f, 1.0f } };
            g_sceneColor = CreateTarget(g_backBufferFormat, kWidth, kHeight, g_options.msaa, D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
                D3D12_RESOURCE_STATE_RENDER_TARGET, &clear, L"Scene Color MSAA");
            g_device->CreateRenderTargetView(g_sceneColor.Get(), nullptr, RtvHandle(kFrameCount));
        }
        if (g_options.mrt > 1)
        {
            D3D12_CLEAR_VALUE clear = { kExtraTargetFormat, { 0.0f, 0.0f, 0.0f, 0.0f } };
            g_sceneExtra = CreateTarget(kExtraTargetFormat, kWidth, kHeight, g_options.msaa, D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
                D3D12_RESOURCE_STATE_RENDER_TARGET, &clear, L"Scene Extra MRT1");
            g_device->CreateRenderTargetView(g_sceneExtra.Get(), nullptr, RtvHandle(kFrameCount + 1));
        }
        if (g_options.depth)
        {
            D3D12_DESCRIPTOR_HEAP_DESC dsvHeapDesc = {};
            dsvHeapDesc.NumDescriptors = 1;
            dsvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
            Check(g_device->CreateDescriptorHeap(&dsvHeapDesc, IID_PPV_ARGS(&g_dsvHeap)), "CreateDescriptorHeap (DSV)");
            SetName(g_dsvHeap.Get(), L"DSV Heap");
            D3D12_CLEAR_VALUE clear = {};
            clear.Format = DXGI_FORMAT_D32_FLOAT;
            clear.DepthStencil.Depth = 1.0f;
            g_depthBuffer = CreateTarget(DXGI_FORMAT_D32_FLOAT, kWidth, kHeight, g_options.msaa, D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL,
                D3D12_RESOURCE_STATE_DEPTH_WRITE, &clear, L"Depth Buffer");
            g_device->CreateDepthStencilView(g_depthBuffer.Get(), nullptr, g_dsvHeap->GetCPUDescriptorHandleForHeapStart());
        }
        if (g_options.perf)
        {
            D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
            heapDesc.NumDescriptors = 1;
            heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
            Check(g_device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&g_perfRtvHeap)), "CreateDescriptorHeap (perf RTV)");
            SetName(g_perfRtvHeap.Get(), L"Perf RTV Heap");
            heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_DSV;
            Check(g_device->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&g_perfDsvHeap)), "CreateDescriptorHeap (perf DSV)");
            SetName(g_perfDsvHeap.Get(), L"Perf DSV Heap");
            D3D12_CLEAR_VALUE colorClear = { DXGI_FORMAT_R8G8B8A8_UNORM, { 0.02f, 0.02f, 0.05f, 1.0f } };
            g_perfColor = CreateTarget(DXGI_FORMAT_R8G8B8A8_UNORM, kPerfWidth, kPerfHeight, 1, D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
                D3D12_RESOURCE_STATE_RENDER_TARGET, &colorClear, L"Perf Scene Color 1080p");
            g_device->CreateRenderTargetView(g_perfColor.Get(), nullptr, g_perfRtvHeap->GetCPUDescriptorHandleForHeapStart());
            D3D12_CLEAR_VALUE depthClear = {};
            depthClear.Format = DXGI_FORMAT_D32_FLOAT;
            depthClear.DepthStencil.Depth = 1.0f;
            g_perfDepth = CreateTarget(DXGI_FORMAT_D32_FLOAT, kPerfWidth, kPerfHeight, 1, D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL,
                D3D12_RESOURCE_STATE_DEPTH_WRITE, &depthClear, L"Perf Depth 1080p");
            g_device->CreateDepthStencilView(g_perfDepth.Get(), nullptr, g_perfDsvHeap->GetCPUDescriptorHandleForHeapStart());

            D3D12_COMMAND_QUEUE_DESC copyDesc = {};
            copyDesc.Type = D3D12_COMMAND_LIST_TYPE_COPY;
            Check(g_device->CreateCommandQueue(&copyDesc, IID_PPV_ARGS(&g_copyQueue)), "CreateCommandQueue (copy)");
            SetName(g_copyQueue.Get(), L"Copy Queue");
            Check(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COPY, IID_PPV_ARGS(&g_copyAllocator)), "CreateCommandAllocator (copy)");
            Check(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COPY, g_copyAllocator.Get(), nullptr, IID_PPV_ARGS(&g_copyList)), "CreateCommandList (copy)");
            SetName(g_copyList.Get(), L"Copy Command List");
            Check(g_copyList->Close(), "Close (copy)");
            g_copyTarget = CreateBuffer(sizeof(Vertex) * 3, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, L"Copy Queue Target");
            Check(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_copyFence)), "CreateFence (copy)");
            SetName(g_copyFence.Get(), L"Copy Fence");
            g_copyFenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        }
        if (g_options.placedHeap)
        {
            D3D12_HEAP_DESC heapDesc = {};
            heapDesc.SizeInBytes = 8u << 20;
            heapDesc.Properties.Type = D3D12_HEAP_TYPE_DEFAULT;
            Check(g_device->CreateHeap(&heapDesc, IID_PPV_ARGS(&g_placedHeap)), "CreateHeap (placed)");
            SetName(g_placedHeap.Get(), L"Placed Heap");
            D3D12_RESOURCE_DESC bufferDesc = {};
            bufferDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            bufferDesc.Width = 1u << 20;
            bufferDesc.Height = 1;
            bufferDesc.DepthOrArraySize = 1;
            bufferDesc.MipLevels = 1;
            bufferDesc.SampleDesc.Count = 1;
            bufferDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            Check(g_device->CreatePlacedResource(g_placedHeap.Get(), 0, &bufferDesc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g_placedBuffer)), "CreatePlacedResource (buffer)");
            SetName(g_placedBuffer.Get(), L"Placed Buffer");
            D3D12_RESOURCE_DESC textureDesc = {};
            textureDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
            textureDesc.Width = 256;
            textureDesc.Height = 256;
            textureDesc.DepthOrArraySize = 1;
            textureDesc.MipLevels = 1;
            textureDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            textureDesc.SampleDesc.Count = 1;
            D3D12_RESOURCE_ALLOCATION_INFO bufferInfo = g_device->GetResourceAllocationInfo(0, 1, &bufferDesc);
            UINT64 textureOffset = (bufferInfo.SizeInBytes + 0xFFFFull) & ~0xFFFFull;
            Check(g_device->CreatePlacedResource(g_placedHeap.Get(), textureOffset, &textureDesc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g_placedTexture)), "CreatePlacedResource (texture)");
            SetName(g_placedTexture.Get(), L"Placed Texture");
        }
        if (g_options.reserved)
        {
            D3D12_FEATURE_DATA_D3D12_OPTIONS features = {};
            if (FAILED(g_device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &features, sizeof(features)))
                || features.TiledResourcesTier == D3D12_TILED_RESOURCES_TIER_NOT_SUPPORTED)
            {
                SkipFeature("--reserved", "tiled resources are not supported");
                g_options.reserved = false;
            }
            else
            {
                D3D12_RESOURCE_DESC desc = {};
                desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
                desc.Width = 4096;
                desc.Height = 4096;
                desc.DepthOrArraySize = 1;
                desc.MipLevels = 1;
                desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
                desc.SampleDesc.Count = 1;
                desc.Layout = D3D12_TEXTURE_LAYOUT_64KB_UNDEFINED_SWIZZLE;
                Check(g_device->CreateReservedResource(&desc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&g_reservedTexture)), "CreateReservedResource");
                SetName(g_reservedTexture.Get(), L"Reserved Texture");
                D3D12_HEAP_DESC heapDesc = {};
                heapDesc.SizeInBytes = 4u * 65536u;
                heapDesc.Properties.Type = D3D12_HEAP_TYPE_DEFAULT;
                Check(g_device->CreateHeap(&heapDesc, IID_PPV_ARGS(&g_tileHeap)), "CreateHeap (tiles)");
                SetName(g_tileHeap.Get(), L"Reserved Tile Heap");
                D3D12_TILED_RESOURCE_COORDINATE start = {};
                D3D12_TILE_REGION_SIZE region = {};
                region.NumTiles = 4;
                region.UseBox = TRUE;
                region.Width = 2;
                region.Height = 2;
                region.Depth = 1;
                D3D12_TILE_RANGE_FLAGS flags = D3D12_TILE_RANGE_FLAG_NONE;
                UINT heapStart = 0, tileCount = 4;
                g_queue->UpdateTileMappings(g_reservedTexture.Get(), 1, &start, &region, g_tileHeap.Get(), 1, &flags, &heapStart, &tileCount, D3D12_TILE_MAPPING_FLAG_NONE);
            }
        }
        if (g_options.indirect)
        {
            D3D12_INDIRECT_ARGUMENT_DESC argument = {};
            argument.Type = D3D12_INDIRECT_ARGUMENT_TYPE_DRAW;
            D3D12_COMMAND_SIGNATURE_DESC signatureDesc = { sizeof(D3D12_DRAW_ARGUMENTS), 1, &argument };
            Check(g_device->CreateCommandSignature(&signatureDesc, nullptr, IID_PPV_ARGS(&g_drawSignature)), "CreateCommandSignature");
            SetName(g_drawSignature.Get(), L"Draw Command Signature");
        }

        // Graphics root signature: CBV b0 (vertex), descriptor table SRV t0 (pixel), DWORDs b1, sampler s0.
        D3D12_DESCRIPTOR_RANGE srvRange = {};
        srvRange.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        srvRange.NumDescriptors = 1;
        srvRange.BaseShaderRegister = 0;
        D3D12_ROOT_PARAMETER params[3] = {};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        params[0].Descriptor.ShaderRegister = 0;
        params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_VERTEX;
        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[1].DescriptorTable.NumDescriptorRanges = 1;
        params[1].DescriptorTable.pDescriptorRanges = &srvRange;
        params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        params[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        params[2].Constants.ShaderRegister = 1;
        params[2].Constants.Num32BitValues = 4;
        params[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        D3D12_STATIC_SAMPLER_DESC sampler = {};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
        sampler.ShaderRegister = 0;
        sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        D3D12_ROOT_SIGNATURE_DESC rsDesc = {};
        rsDesc.NumParameters = 3;
        rsDesc.pParameters = params;
        rsDesc.NumStaticSamplers = 1;
        rsDesc.pStaticSamplers = &sampler;
        rsDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;
        ComPtr<ID3DBlob> signature, error;
        Check(D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error), "D3D12SerializeRootSignature");
        Check(g_device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&g_rootSignature)), "CreateRootSignature");
        SetName(g_rootSignature.Get(), L"Triangle Root Signature");

        // Compute root signature: UAV u0 (root descriptor) and one 32-bit constant b0.
        D3D12_ROOT_PARAMETER computeParams[2] = {};
        computeParams[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
        computeParams[0].Descriptor.ShaderRegister = 0;
        computeParams[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        computeParams[1].Constants.ShaderRegister = 0;
        computeParams[1].Constants.Num32BitValues = 1;
        D3D12_ROOT_SIGNATURE_DESC computeRs = {};
        computeRs.NumParameters = 2;
        computeRs.pParameters = computeParams;
        Check(D3D12SerializeRootSignature(&computeRs, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error), "D3D12SerializeRootSignature (compute)");
        Check(g_device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&g_computeRootSignature)), "CreateRootSignature (compute)");
        SetName(g_computeRootSignature.Get(), L"Compute Root Signature");

        ShaderCode vs = CompileShader(kShader, "VSMain", "vs");
        std::string richShader = std::string(kShader) + kRichShaderExtra;
        ShaderCode ps = g_options.mrt > 1 ? CompileShader(richShader.c_str(), "PSMainMrt", "ps")
            : CompileShader(kShader, g_candidate ? "PSMainCandidate" : "PSMain", "ps");
        ShaderCode cs = CompileShader(kComputeShader, "CSMain", "cs");
        ShaderCode csHang = CompileShader(kComputeShader, "CSHang", "cs");

        D3D12_INPUT_ELEMENT_DESC layout[] =
        {
            { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
            { "COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
            { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 28, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
        };
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pso = {};
        pso.InputLayout = { layout, 3 };
        pso.pRootSignature = g_rootSignature.Get();
        pso.VS = vs.View();
        pso.PS = ps.View();
        pso.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
        pso.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
        pso.RasterizerState.DepthClipEnable = TRUE;
        for (UINT target = 0; target < g_options.mrt; ++target) pso.BlendState.RenderTarget[target].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        pso.SampleMask = UINT_MAX;
        pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pso.NumRenderTargets = g_options.mrt;
        pso.RTVFormats[0] = g_backBufferFormat;
        if (g_options.mrt > 1) pso.RTVFormats[1] = kExtraTargetFormat;
        pso.SampleDesc.Count = g_options.msaa;
        if (g_options.depth)
        {
            pso.DSVFormat = DXGI_FORMAT_D32_FLOAT;
            pso.DepthStencilState.DepthEnable = TRUE;
            pso.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
            pso.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_LESS;
        }
        Check(g_device->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&g_pipelineState)), "CreateGraphicsPipelineState");
        SetName(g_pipelineState.Get(), L"Triangle PSO");

        D3D12_COMPUTE_PIPELINE_STATE_DESC cpso = {};
        cpso.pRootSignature = g_computeRootSignature.Get();
        cpso.CS = cs.View();
        Check(g_device->CreateComputePipelineState(&cpso, IID_PPV_ARGS(&g_computeState)), "CreateComputePipelineState");
        SetName(g_computeState.Get(), L"Wave PSO");
        cpso.CS = csHang.View();
        Check(g_device->CreateComputePipelineState(&cpso, IID_PPV_ARGS(&g_hangState)), "CreateComputePipelineState (hang)");
        SetName(g_hangState.Get(), L"Hang PSO");
        if (g_options.asyncOverlap)
        {
            ShaderCode heavy = CompileShader(kHeavyComputeShader, "CSHeavy", "cs");
            cpso.CS = heavy.View();
            Check(g_device->CreateComputePipelineState(&cpso, IID_PPV_ARGS(&g_heavyComputeState)), "CreateComputePipelineState (heavy)");
            SetName(g_heavyComputeState.Get(), L"Heavy Compute PSO");
        }
        if (g_options.bandwidth)
        {
            ShaderCode bandwidth = CompileShader(richShader.c_str(), "PSBandwidth", "ps");
            pso.PS = bandwidth.View();
            Check(g_device->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&g_bandwidthState)), "CreateGraphicsPipelineState (bandwidth)");
            SetName(g_bandwidthState.Get(), L"Bandwidth PSO");
        }
        if (g_options.perf)
        {
            ShaderCode scenePs = CompileShader(kShader, "PSMain", "ps");
            ShaderCode lightingPs = CompileShader(richShader.c_str(), "PSLighting", "ps");
            D3D12_GRAPHICS_PIPELINE_STATE_DESC perf = pso;
            perf.PS = scenePs.View();
            perf.NumRenderTargets = 1;
            perf.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
            perf.RTVFormats[1] = DXGI_FORMAT_UNKNOWN;
            perf.SampleDesc.Count = 1;
            perf.DSVFormat = DXGI_FORMAT_D32_FLOAT;
            perf.DepthStencilState.DepthEnable = TRUE;
            perf.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ALL;
            perf.DepthStencilState.DepthFunc = D3D12_COMPARISON_FUNC_LESS;
            Check(g_device->CreateGraphicsPipelineState(&perf, IID_PPV_ARGS(&g_perfState)), "CreateGraphicsPipelineState (perf)");
            SetName(g_perfState.Get(), L"Perf Scene PSO");
            perf.PS = lightingPs.View();
            perf.DepthStencilState.DepthEnable = FALSE;
            perf.DepthStencilState.DepthWriteMask = D3D12_DEPTH_WRITE_MASK_ZERO;
            Check(g_device->CreateGraphicsPipelineState(&perf, IID_PPV_ARGS(&g_lightingState)), "CreateGraphicsPipelineState (lighting)");
            SetName(g_lightingState.Get(), L"Lighting PSO");
            perf.PS = scenePs.View();
            perf.RTVFormats[0] = g_backBufferFormat;
            perf.DSVFormat = DXGI_FORMAT_UNKNOWN;
            Check(g_device->CreateGraphicsPipelineState(&perf, IID_PPV_ARGS(&g_postState)), "CreateGraphicsPipelineState (post)");
            SetName(g_postState.Get(), L"Post PSO");
        }
        if (g_options.mesh)
        {
            ComPtr<ID3D12Device2> device2;
            D3D12_FEATURE_DATA_D3D12_OPTIONS7 options7 = {};
            if (FAILED(g_device.As(&device2)) || FAILED(g_device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS7, &options7, sizeof(options7)))
                || options7.MeshShaderTier == D3D12_MESH_SHADER_TIER_NOT_SUPPORTED)
            {
                SkipFeature("--mesh", "mesh shaders are not supported");
                g_options.mesh = false;
            }
            else
            {
                D3D12_ROOT_SIGNATURE_DESC meshRs = {};
                Check(D3D12SerializeRootSignature(&meshRs, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error), "D3D12SerializeRootSignature (mesh)");
                Check(g_device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&g_meshRootSignature)), "CreateRootSignature (mesh)");
                SetName(g_meshRootSignature.Get(), L"Mesh Root Signature");
                ShaderCode ms = CompileShader(kMeshShader, "MSMain", "ms");
                ShaderCode meshPs = CompileShader(kMeshShader, "PSMesh", "psm");
                struct
                {
                    StreamItem<ID3D12RootSignature*, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_ROOT_SIGNATURE> rootSignature;
                    StreamItem<D3D12_SHADER_BYTECODE, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_MS> ms;
                    StreamItem<D3D12_SHADER_BYTECODE, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PS> ps;
                    StreamItem<D3D12_BLEND_DESC, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_BLEND> blend;
                    StreamItem<D3D12_RASTERIZER_DESC, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RASTERIZER> rasterizer;
                    StreamItem<D3D12_DEPTH_STENCIL_DESC, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL> depth;
                    StreamItem<D3D12_RT_FORMAT_ARRAY, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_RENDER_TARGET_FORMATS> formats;
                    StreamItem<DXGI_FORMAT, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_DEPTH_STENCIL_FORMAT> depthFormat;
                    StreamItem<DXGI_SAMPLE_DESC, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_DESC> samples;
                    StreamItem<UINT, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_SAMPLE_MASK> sampleMask;
                    StreamItem<D3D12_PRIMITIVE_TOPOLOGY_TYPE, D3D12_PIPELINE_STATE_SUBOBJECT_TYPE_PRIMITIVE_TOPOLOGY> topology;
                } stream;
                stream.rootSignature.value = g_meshRootSignature.Get();
                stream.ms.value = ms.View();
                stream.ps.value = meshPs.View();
                stream.blend.value = pso.BlendState;
                stream.rasterizer.value = pso.RasterizerState;
                stream.depth.value = pso.DepthStencilState;
                stream.formats.value.NumRenderTargets = pso.NumRenderTargets;
                for (UINT target = 0; target < 8; ++target) stream.formats.value.RTFormats[target] = pso.RTVFormats[target];
                stream.depthFormat.value = pso.DSVFormat;
                stream.samples.value = pso.SampleDesc;
                stream.sampleMask.value = UINT_MAX;
                stream.topology.value = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
                D3D12_PIPELINE_STATE_STREAM_DESC streamDesc = { sizeof(stream), &stream };
                Check(device2->CreatePipelineState(&streamDesc, IID_PPV_ARGS(&g_meshState)), "CreatePipelineState (mesh)");
                SetName(g_meshState.Get(), L"Mesh PSO");
            }
        }

        Check(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_allocators[g_frameIndex].Get(), g_pipelineState.Get(), IID_PPV_ARGS(&g_commandList)), "CreateCommandList");
        SetName(g_commandList.Get(), L"Frame Command List");
        Check(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, g_computeAllocator.Get(), g_computeState.Get(), IID_PPV_ARGS(&g_computeList)), "CreateCommandList (compute)");
        SetName(g_computeList.Get(), L"Compute Command List");
        Check(g_computeList->Close(), "Close (compute)");

        Vertex vertices[] =
        {
            { { 0.0f, 0.5f, 0.0f }, { 1.0f, 0.0f, 0.0f, 1.0f }, { 0.5f, 0.0f } },
            { { 0.5f, -0.5f, 0.0f }, { 0.0f, 1.0f, 0.0f, 1.0f }, { 1.0f, 1.0f } },
            { { -0.5f, -0.5f, 0.0f }, { 0.0f, 0.0f, 1.0f, 1.0f }, { 0.0f, 1.0f } },
        };
        g_vertexBuffer = CreateUploadBuffer(sizeof(vertices), L"Triangle Vertex Buffer");
        void* mapped = nullptr;
        D3D12_RANGE noRead = { 0, 0 };
        Check(g_vertexBuffer->Map(0, &noRead, &mapped), "Map VB");
        std::memcpy(mapped, vertices, sizeof(vertices));
        g_vertexBuffer->Unmap(0, nullptr);
        g_vbv.BufferLocation = g_vertexBuffer->GetGPUVirtualAddress();
        g_vbv.StrideInBytes = sizeof(Vertex);
        g_vbv.SizeInBytes = sizeof(vertices);

        const UINT16 indices[] = { 0, 1, 2 };
        g_indexBuffer = CreateUploadBuffer(sizeof(indices), L"Triangle Index Buffer");
        Check(g_indexBuffer->Map(0, &noRead, &mapped), "Map IB");
        std::memcpy(mapped, indices, sizeof(indices));
        g_indexBuffer->Unmap(0, nullptr);
        g_ibv.BufferLocation = g_indexBuffer->GetGPUVirtualAddress();
        g_ibv.Format = DXGI_FORMAT_R16_UINT;
        g_ibv.SizeInBytes = sizeof(indices);

        if (g_options.depth)
        {
            // Two overlapping triangles: the nearer one is drawn first, so the farther one fails the depth test.
            Vertex depthVertices[] =
            {
                { { 0.55f, 0.85f, 0.25f }, { 1.0f, 1.0f, 0.0f, 1.0f }, { 0.5f, 0.0f } },
                { { 0.95f, 0.15f, 0.25f }, { 1.0f, 1.0f, 0.0f, 1.0f }, { 1.0f, 1.0f } },
                { { 0.15f, 0.15f, 0.25f }, { 1.0f, 1.0f, 0.0f, 1.0f }, { 0.0f, 1.0f } },
                { { 0.55f, 0.75f, 0.75f }, { 0.0f, 1.0f, 1.0f, 1.0f }, { 0.5f, 0.0f } },
                { { 0.85f, 0.25f, 0.75f }, { 0.0f, 1.0f, 1.0f, 1.0f }, { 1.0f, 1.0f } },
                { { 0.25f, 0.25f, 0.75f }, { 0.0f, 1.0f, 1.0f, 1.0f }, { 0.0f, 1.0f } },
            };
            g_depthVertexBuffer = CreateUploadBuffer(sizeof(depthVertices), L"Depth Test Vertex Buffer");
            Check(g_depthVertexBuffer->Map(0, &noRead, &mapped), "Map depth VB");
            std::memcpy(mapped, depthVertices, sizeof(depthVertices));
            g_depthVertexBuffer->Unmap(0, nullptr);
            g_depthVbv.BufferLocation = g_depthVertexBuffer->GetGPUVirtualAddress();
            g_depthVbv.StrideInBytes = sizeof(Vertex);
            g_depthVbv.SizeInBytes = sizeof(depthVertices);
        }

        if (g_options.indirect)
        {
            const D3D12_DRAW_ARGUMENTS arguments[4] = { { 3, 1, 0, 0 }, { 3, 2, 0, 0 }, { 3, 1, 0, 1 }, { 3, 3, 0, 0 } };
            g_indirectArguments = CreateUploadBuffer(sizeof(arguments), L"Indirect Draw Arguments");
            Check(g_indirectArguments->Map(0, &noRead, &mapped), "Map indirect arguments");
            std::memcpy(mapped, arguments, sizeof(arguments));
            g_indirectArguments->Unmap(0, nullptr);
        }
        if (g_options.bandwidth || g_options.perf)
        {
            Vertex fullscreen[] =
            {
                { { -6.0f, -4.0f, 0.5f }, { 1.0f, 1.0f, 1.0f, 1.0f }, { 0.0f, 1.0f } },
                { { 6.0f, -4.0f, 0.5f }, { 1.0f, 1.0f, 1.0f, 1.0f }, { 1.0f, 1.0f } },
                { { 0.0f, 6.0f, 0.5f }, { 1.0f, 1.0f, 1.0f, 1.0f }, { 0.5f, 0.0f } },
            };
            g_fullscreenVertexBuffer = CreateUploadBuffer(sizeof(fullscreen), L"Fullscreen Vertex Buffer");
            Check(g_fullscreenVertexBuffer->Map(0, &noRead, &mapped), "Map fullscreen VB");
            std::memcpy(mapped, fullscreen, sizeof(fullscreen));
            g_fullscreenVertexBuffer->Unmap(0, nullptr);
            g_fullscreenVbv.BufferLocation = g_fullscreenVertexBuffer->GetGPUVirtualAddress();
            g_fullscreenVbv.StrideInBytes = sizeof(Vertex);
            g_fullscreenVbv.SizeInBytes = sizeof(fullscreen);
        }
        if (g_options.bandwidth)
        {
            g_bandwidthTexture = CreateTarget(DXGI_FORMAT_R16G16B16A16_FLOAT, 2048, 2048, 1, D3D12_RESOURCE_FLAG_NONE,
                D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, nullptr, L"Bandwidth Texture");
            D3D12_SHADER_RESOURCE_VIEW_DESC srv = {};
            srv.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            srv.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            srv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            srv.Texture2D.MipLevels = 1;
            D3D12_CPU_DESCRIPTOR_HANDLE slot = g_srvHeap->GetCPUDescriptorHandleForHeapStart();
            slot.ptr += g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
            g_device->CreateShaderResourceView(g_bandwidthTexture.Get(), &srv, slot);
        }

        g_constantBuffer = CreateUploadBuffer(sizeof(Constants) * kFrameCount, L"Per-frame Constants");
        Check(g_constantBuffer->Map(0, &noRead, reinterpret_cast<void**>(&g_mappedConstants)), "Map CB");

        g_computeOutput = CreateBuffer(kComputeElements * (g_candidate ? 2 : 1) * sizeof(float), D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, L"Compute Output");

        // Upload the texture on the graphics command list before the first frame.
        CreateTexture(g_commandList.Get());
        Check(g_commandList->Close(), "Close");
        ID3D12CommandList* lists[] = { g_commandList.Get() };
        g_queue->ExecuteCommandLists(1, lists);

        Check(g_device->CreateFence(g_fenceValues[g_frameIndex], D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_fence)), "CreateFence");
        SetName(g_fence.Get(), L"Frame Fence");
        g_fenceValues[g_frameIndex]++;
        g_fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        Check(g_device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_computeFence)), "CreateFence (compute)");
        SetName(g_computeFence.Get(), L"Compute Fence");
        g_computeFenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        WaitForGpu();
    }

    void RunCompute(UINT frame, bool hang)
    {
        WaitForCompute();
        Check(g_computeAllocator->Reset(), "Compute allocator Reset");
        ID3D12PipelineState* computeState = hang ? g_hangState.Get() : g_heavyComputeState ? g_heavyComputeState.Get() : g_computeState.Get();
        Check(g_computeList->Reset(g_computeAllocator.Get(), computeState), "Compute list Reset");
        BeginEvent(g_computeList.Get(), hang ? L"Hang dispatch" : g_heavyComputeState ? L"Heavy compute" : L"Wave compute");
        g_computeList->SetComputeRootSignature(g_computeRootSignature.Get());
        g_computeList->SetComputeRootUnorderedAccessView(0, g_computeOutput->GetGPUVirtualAddress());
        // Fixed values make root-constant and resource comparisons independent of capture frame.
        float angle = g_candidate ? 0.75f : 0.5f;
        g_computeList->SetComputeRoot32BitConstants(1, 1, &angle, 0);
        g_computeList->Dispatch(kComputeElements * (g_candidate ? 2 : 1) / 64, 1, 1);
        EndEvent(g_computeList.Get());
        Check(g_computeList->Close(), "Compute list Close");
        ID3D12CommandList* lists[] = { g_computeList.Get() };
        g_computeQueue->ExecuteCommandLists(1, lists);
        Check(g_computeQueue->Signal(g_computeFence.Get(), ++g_computeFenceValue), "Compute Signal");
    }

    void RunCopyUpload()
    {
        if (g_copyFence->GetCompletedValue() < g_copyFenceValue)
        {
            Check(g_copyFence->SetEventOnCompletion(g_copyFenceValue, g_copyFenceEvent), "SetEventOnCompletion (copy)");
            WaitForSingleObjectEx(g_copyFenceEvent, INFINITE, FALSE);
        }
        Check(g_copyAllocator->Reset(), "Copy allocator Reset");
        Check(g_copyList->Reset(g_copyAllocator.Get(), nullptr), "Copy list Reset");
        BeginEvent(g_copyList.Get(), L"Copy upload");
        g_copyList->CopyBufferRegion(g_copyTarget.Get(), 0, g_vertexBuffer.Get(), 0, sizeof(Vertex) * 3);
        EndEvent(g_copyList.Get());
        Check(g_copyList->Close(), "Copy list Close");
        ID3D12CommandList* lists[] = { g_copyList.Get() };
        g_copyQueue->ExecuteCommandLists(1, lists);
        Check(g_copyQueue->Signal(g_copyFence.Get(), ++g_copyFenceValue), "Copy Signal");
    }

    // --workload perf: Frame > Shadow, GBuffer (with ExecuteIndirect), Lighting (the dominant pass), Post into the back buffer.
    void RenderPerf(UINT frame)
    {
        Constants& c = g_mappedConstants[g_frameIndex];
        c.angle = 0.5f;
        c.aspect = static_cast<float>(kPerfWidth) / kPerfHeight;

        Check(g_allocators[g_frameIndex]->Reset(), "Allocator Reset");
        Check(g_commandList->Reset(g_allocators[g_frameIndex].Get(), g_perfState.Get()), "CommandList Reset");
        BeginEvent(g_commandList.Get(), L"Frame");
        ID3D12DescriptorHeap* heaps[] = { g_srvHeap.Get() };
        g_commandList->SetDescriptorHeaps(1, heaps);
        g_commandList->SetGraphicsRootSignature(g_rootSignature.Get());
        g_commandList->SetGraphicsRootConstantBufferView(0, g_constantBuffer->GetGPUVirtualAddress() + g_frameIndex * sizeof(Constants));
        g_commandList->SetGraphicsRootDescriptorTable(1, g_srvHeap->GetGPUDescriptorHandleForHeapStart());
        const UINT drawConstants[] = { 0x50495831u, g_candidate ? 2u : 1u, 0xffffffffu, 0x3f800000u };
        g_commandList->SetGraphicsRoot32BitConstants(2, 4, drawConstants, 0);
        D3D12_VIEWPORT viewport = { 0.0f, 0.0f, static_cast<float>(kPerfWidth), static_cast<float>(kPerfHeight), 0.0f, 1.0f };
        D3D12_RECT scissor = { 0, 0, static_cast<LONG>(kPerfWidth), static_cast<LONG>(kPerfHeight) };
        g_commandList->RSSetViewports(1, &viewport);
        g_commandList->RSSetScissorRects(1, &scissor);
        D3D12_CPU_DESCRIPTOR_HANDLE color = g_perfRtvHeap->GetCPUDescriptorHandleForHeapStart();
        D3D12_CPU_DESCRIPTOR_HANDLE depth = g_perfDsvHeap->GetCPUDescriptorHandleForHeapStart();
        g_commandList->OMSetRenderTargets(1, &color, FALSE, &depth);
        const float clear[] = { 0.02f, 0.02f, 0.05f, 1.0f };
        g_commandList->ClearRenderTargetView(color, clear, 0, nullptr);
        g_commandList->ClearDepthStencilView(depth, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
        g_commandList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

        BeginEvent(g_commandList.Get(), L"Shadow");
        g_commandList->IASetVertexBuffers(0, 1, &g_depthVbv);
        g_commandList->DrawInstanced(3, 1, 0, 0);
        g_commandList->DrawInstanced(3, 1, 3, 0);
        EndEvent(g_commandList.Get());

        BeginEvent(g_commandList.Get(), L"GBuffer");
        g_commandList->IASetVertexBuffers(0, 1, &g_vbv);
        g_commandList->IASetIndexBuffer(&g_ibv);
        g_commandList->DrawInstanced(3, 1, 0, 0);
        g_commandList->DrawIndexedInstanced(3, 1, 0, 0, 0);
        g_commandList->ExecuteIndirect(g_drawSignature.Get(), 4, g_indirectArguments.Get(), 0, nullptr, 0);
        EndEvent(g_commandList.Get());

        BeginEvent(g_commandList.Get(), L"Lighting");
        g_commandList->SetPipelineState(g_lightingState.Get());
        g_commandList->IASetVertexBuffers(0, 1, &g_fullscreenVbv);
        g_commandList->DrawInstanced(3, 1, 0, 0);
        EndEvent(g_commandList.Get());

        BeginEvent(g_commandList.Get(), L"Post");
        ID3D12Resource* backBuffer = g_renderTargets[g_frameIndex].Get();
        Transition(g_commandList.Get(), backBuffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
        D3D12_VIEWPORT postViewport = { 0.0f, 0.0f, static_cast<float>(kWidth), static_cast<float>(kHeight), 0.0f, 1.0f };
        D3D12_RECT postScissor = { 0, 0, static_cast<LONG>(kWidth), static_cast<LONG>(kHeight) };
        g_commandList->RSSetViewports(1, &postViewport);
        g_commandList->RSSetScissorRects(1, &postScissor);
        D3D12_CPU_DESCRIPTOR_HANDLE output = RtvHandle(g_frameIndex);
        g_commandList->OMSetRenderTargets(1, &output, FALSE, nullptr);
        g_commandList->SetPipelineState(g_postState.Get());
        g_commandList->ClearRenderTargetView(output, clear, 0, nullptr);
        g_commandList->DrawInstanced(3, 1, 0, 0);
        Transition(g_commandList.Get(), backBuffer, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
        EndEvent(g_commandList.Get());
        EndEvent(g_commandList.Get());

        Check(g_commandList->Close(), "Close");
        ID3D12CommandList* lists[] = { g_commandList.Get() };
        Check(g_queue->Wait(g_computeFence.Get(), g_computeFenceValue), "Wait (compute fence)");
        g_queue->ExecuteCommandLists(1, lists);
        RunCopyUpload();
        Check(g_swapChain->Present(1, 0), "Present");
        MoveToNextFrame();
    }

    void Render(UINT frame)
    {
        Constants& c = g_mappedConstants[g_frameIndex];
        c.angle = 0.5f;
        c.aspect = static_cast<float>(kWidth) / kHeight;

        Check(g_allocators[g_frameIndex]->Reset(), "Allocator Reset");
        Check(g_commandList->Reset(g_allocators[g_frameIndex].Get(), g_pipelineState.Get()), "CommandList Reset");

        BeginEvent(g_commandList.Get(), L"Frame");

        ID3D12DescriptorHeap* heaps[] = { g_srvHeap.Get() };
        g_commandList->SetDescriptorHeaps(1, heaps);
        g_commandList->SetGraphicsRootSignature(g_rootSignature.Get());
        g_commandList->SetGraphicsRootConstantBufferView(0, g_constantBuffer->GetGPUVirtualAddress() + g_frameIndex * sizeof(Constants));
        g_commandList->SetGraphicsRootDescriptorTable(1, g_srvHeap->GetGPUDescriptorHandleForHeapStart());
        // b1 = [magic PIX1, variant, flags, IEEE-754 1.0]. All four slots are used by the pixel shader.
        const UINT drawConstants[] = { 0x50495831u, g_candidate ? 2u : 1u, 0xffffffffu, 0x3f800000u };
        g_commandList->SetGraphicsRoot32BitConstants(2, 4, drawConstants, 0);
        D3D12_VIEWPORT viewport = { 0.0f, 0.0f, static_cast<float>(kWidth), static_cast<float>(kHeight), 0.0f, 1.0f };
        D3D12_RECT scissor = { 0, 0, static_cast<LONG>(kWidth), static_cast<LONG>(kHeight) };
        g_commandList->RSSetViewports(1, &viewport);
        g_commandList->RSSetScissorRects(1, &scissor);

        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = g_renderTargets[g_frameIndex].Get();
        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        g_commandList->ResourceBarrier(1, &barrier);

        // Default: the back buffer alone. Rich flags draw into the MSAA scene target, add a second target and depth.
        D3D12_CPU_DESCRIPTOR_HANDLE targets[2] = { g_sceneColor ? RtvHandle(kFrameCount) : RtvHandle(g_frameIndex), RtvHandle(kFrameCount + 1) };
        D3D12_CPU_DESCRIPTOR_HANDLE dsv = g_depthBuffer ? g_dsvHeap->GetCPUDescriptorHandleForHeapStart() : D3D12_CPU_DESCRIPTOR_HANDLE{};
        g_commandList->OMSetRenderTargets(g_options.mrt, targets, FALSE, g_depthBuffer ? &dsv : nullptr);

        BeginEvent(g_commandList.Get(), L"Clear");
        const float clear[] = { 0.05f, 0.05f, 0.15f, 1.0f };
        g_commandList->ClearRenderTargetView(targets[0], clear, 0, nullptr);
        if (g_sceneExtra)
        {
            const float zero[] = { 0.0f, 0.0f, 0.0f, 0.0f };
            g_commandList->ClearRenderTargetView(targets[1], zero, 0, nullptr);
        }
        if (g_depthBuffer) g_commandList->ClearDepthStencilView(dsv, D3D12_CLEAR_FLAG_DEPTH, 1.0f, 0, 0, nullptr);
        EndEvent(g_commandList.Get());

        BeginEvent(g_commandList.Get(), L"Triangle pass");
        SetMarker(g_commandList.Get(), L"Hello PixMcp!!!");
        g_commandList->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        g_commandList->IASetVertexBuffers(0, 1, &g_vbv);
        g_commandList->IASetIndexBuffer(&g_ibv);
        if (g_candidate)
        {
            BeginEvent(g_commandList.Get(), L"Candidate extra pass");
            g_commandList->DrawIndexedInstanced(3, 1, 0, 0, 0);
            EndEvent(g_commandList.Get());
        }
        g_commandList->DrawInstanced(3, 1, 0, 0);
        g_commandList->DrawInstanced(3, 1, 0, 0);
        g_commandList->DrawIndexedInstanced(3, 1, 0, 0, 0);
        EndEvent(g_commandList.Get());
        if (g_duplicateMarkers)
        {
            BeginEvent(g_commandList.Get(), L"Triangle pass");
            g_commandList->DrawIndexedInstanced(3, 1, 0, 0, 0);
            EndEvent(g_commandList.Get());
        }

        if (g_drawSignature)
        {
            BeginEvent(g_commandList.Get(), L"Indirect pass");
            g_commandList->ExecuteIndirect(g_drawSignature.Get(), 4, g_indirectArguments.Get(), 0, nullptr, 0);
            EndEvent(g_commandList.Get());
        }
        D3D12_TEXTURE_COPY_LOCATION checkerSource = {};
        checkerSource.pResource = g_textureUpload.Get();
        checkerSource.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        checkerSource.PlacedFootprint = g_checkerFootprint;
        if (g_placedBuffer)
        {
            BeginEvent(g_commandList.Get(), L"Placed pass");
            g_commandList->CopyBufferRegion(g_placedBuffer.Get(), 0, g_vertexBuffer.Get(), 0, sizeof(Vertex) * 3);
            D3D12_TEXTURE_COPY_LOCATION placed = {};
            placed.pResource = g_placedTexture.Get();
            placed.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            g_commandList->CopyTextureRegion(&placed, 0, 0, 0, &checkerSource, nullptr);
            EndEvent(g_commandList.Get());
        }
        if (g_reservedTexture)
        {
            BeginEvent(g_commandList.Get(), L"Reserved pass");
            D3D12_TEXTURE_COPY_LOCATION reserved = {};
            reserved.pResource = g_reservedTexture.Get();
            reserved.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            g_commandList->CopyTextureRegion(&reserved, 0, 0, 0, &checkerSource, nullptr); // lands in the first mapped tile
            EndEvent(g_commandList.Get());
        }
        if (g_bandwidthState)
        {
            BeginEvent(g_commandList.Get(), L"Bandwidth pass");
            D3D12_GPU_DESCRIPTOR_HANDLE table = g_srvHeap->GetGPUDescriptorHandleForHeapStart();
            table.ptr += g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
            g_commandList->SetGraphicsRootDescriptorTable(1, table);
            g_commandList->SetPipelineState(g_bandwidthState.Get());
            g_commandList->IASetVertexBuffers(0, 1, &g_fullscreenVbv);
            g_commandList->DrawInstanced(3, 1, 0, 0);
            g_commandList->SetGraphicsRootDescriptorTable(1, g_srvHeap->GetGPUDescriptorHandleForHeapStart());
            g_commandList->SetPipelineState(g_pipelineState.Get());
            g_commandList->IASetVertexBuffers(0, 1, &g_vbv);
            EndEvent(g_commandList.Get());
        }
        if (g_meshState)
        {
            ComPtr<ID3D12GraphicsCommandList6> list6;
            Check(g_commandList.As(&list6), "ID3D12GraphicsCommandList6");
            BeginEvent(g_commandList.Get(), L"Mesh pass");
            g_commandList->SetGraphicsRootSignature(g_meshRootSignature.Get());
            g_commandList->SetPipelineState(g_meshState.Get());
            list6->DispatchMesh(1, 1, 1);
            // Changing the root signature clears every root binding; restore the triangle state.
            g_commandList->SetGraphicsRootSignature(g_rootSignature.Get());
            g_commandList->SetGraphicsRootConstantBufferView(0, g_constantBuffer->GetGPUVirtualAddress() + g_frameIndex * sizeof(Constants));
            g_commandList->SetGraphicsRootDescriptorTable(1, g_srvHeap->GetGPUDescriptorHandleForHeapStart());
            g_commandList->SetGraphicsRoot32BitConstants(2, 4, drawConstants, 0);
            g_commandList->SetPipelineState(g_pipelineState.Get());
            EndEvent(g_commandList.Get());
        }
        if (g_options.asyncOverlap)
        {
            BeginEvent(g_commandList.Get(), L"Consume compute");
            g_commandList->DrawInstanced(3, 1, 0, 0);
            EndEvent(g_commandList.Get());
        }
        if (g_depthBuffer)
        {
            BeginEvent(g_commandList.Get(), L"Depth pass");
            g_commandList->IASetVertexBuffers(0, 1, &g_depthVbv);
            g_commandList->DrawInstanced(3, 1, 0, 0);
            g_commandList->DrawInstanced(3, 1, 3, 0); // behind the first triangle: rejected by the depth test
            g_commandList->IASetVertexBuffers(0, 1, &g_vbv);
            EndEvent(g_commandList.Get());
        }
        if (g_sceneColor)
        {
            ID3D12Resource* backBuffer = g_renderTargets[g_frameIndex].Get();
            BeginEvent(g_commandList.Get(), L"Resolve pass");
            Transition(g_commandList.Get(), g_sceneColor.Get(), D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_RESOLVE_SOURCE);
            Transition(g_commandList.Get(), backBuffer, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_RESOLVE_DEST);
            g_commandList->ResolveSubresource(backBuffer, 0, g_sceneColor.Get(), 0, g_backBufferFormat);
            Transition(g_commandList.Get(), g_sceneColor.Get(), D3D12_RESOURCE_STATE_RESOLVE_SOURCE, D3D12_RESOURCE_STATE_RENDER_TARGET);
            Transition(g_commandList.Get(), backBuffer, D3D12_RESOURCE_STATE_RESOLVE_DEST, D3D12_RESOURCE_STATE_RENDER_TARGET);
            EndEvent(g_commandList.Get());
        }

        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        g_commandList->ResourceBarrier(1, &barrier);
        EndEvent(g_commandList.Get());

        Check(g_commandList->Close(), "Close");
        ID3D12CommandList* lists[] = { g_commandList.Get() };
        // --async-overlap: the graphics queue waits on the GPU for this frame's compute; the CPU does not.
        if (g_options.asyncOverlap) Check(g_queue->Wait(g_computeFence.Get(), g_computeFenceValue), "Wait (compute fence)");
        g_queue->ExecuteCommandLists(1, lists);
        Check(g_swapChain->Present(1, 0), "Present");
        MoveToNextFrame();
    }
}

static bool ParseBounded(const wchar_t* text, long min, long max, long& value)
{
    wchar_t* end = nullptr;
    value = std::wcstol(text, &end, 10);
    return end != text && *end == L'\0' && value >= min && value <= max;
}

int wmain(int argc, wchar_t** argv)
{
    int maxFrames = -1;
    bool hang = false;
    int hangAfter = 30;
    bool hidden = false;
    int startupDelayMs = 0;
    bool timingWorkload = false;
    for (int i = 1; i < argc; ++i)
    {
        if (std::wcscmp(argv[i], L"--frames") == 0 && i + 1 < argc) maxFrames = _wtoi(argv[++i]);
        else if (std::wcscmp(argv[i], L"--hang") == 0) hang = true;
        else if (std::wcscmp(argv[i], L"--hang-after") == 0 && i + 1 < argc) hangAfter = _wtoi(argv[++i]);
        else if (std::wcscmp(argv[i], L"--variant") == 0 && i + 1 < argc)
        {
            const wchar_t* variant = argv[++i];
            if (std::wcscmp(variant, L"baseline") != 0 && std::wcscmp(variant, L"candidate") != 0)
            {
                std::fprintf(stderr, "--variant must be baseline or candidate.\n");
                return 2;
            }
            g_candidate = std::wcscmp(variant, L"candidate") == 0;
        }
        else if (std::wcscmp(argv[i], L"--duplicate-markers") == 0) g_duplicateMarkers = true;
        else if (std::wcscmp(argv[i], L"--adapter-name") == 0 && i + 1 < argc)
        {
            g_adapterName = argv[++i];
            if (g_adapterName.find_first_not_of(L" \t\r\n") == std::wstring::npos)
            {
                std::fprintf(stderr, "--adapter-name must be a nonempty adapter-name substring.\n");
                return 2;
            }
        }
        else if (std::wcscmp(argv[i], L"--hidden") == 0) hidden = true;
        else if (std::wcscmp(argv[i], L"--timing-workload") == 0) timingWorkload = true;
        else if (std::wcscmp(argv[i], L"--startup-delay-ms") == 0 && i + 1 < argc)
        {
            wchar_t* end = nullptr;
            long value = std::wcstol(argv[++i], &end, 10);
            if (*end != L'\0' || end == argv[i] || value < 0 || value > 60000)
            {
                std::fprintf(stderr, "--startup-delay-ms must be between 0 and 60000.\n");
                return 2;
            }
            startupDelayMs = static_cast<int>(value);
        }
        else if (std::wcscmp(argv[i], L"--depth") == 0) { g_options.depth = true; g_flags.push_back("--depth"); }
        else if (std::wcscmp(argv[i], L"--placed-heap") == 0) { g_options.placedHeap = true; g_flags.push_back("--placed-heap"); }
        else if (std::wcscmp(argv[i], L"--reserved") == 0) { g_options.reserved = true; g_flags.push_back("--reserved"); }
        else if (std::wcscmp(argv[i], L"--indirect") == 0) { g_options.indirect = true; g_flags.push_back("--indirect"); }
        else if (std::wcscmp(argv[i], L"--async-overlap") == 0) { g_options.asyncOverlap = true; g_flags.push_back("--async-overlap"); }
        else if (std::wcscmp(argv[i], L"--bandwidth") == 0) { g_options.bandwidth = true; g_flags.push_back("--bandwidth"); }
        else if (std::wcscmp(argv[i], L"--hdr") == 0) { g_options.hdr = true; g_flags.push_back("--hdr"); }
        else if (std::wcscmp(argv[i], L"--gpu-markers") == 0) { g_options.gpuMarkers = true; g_flags.push_back("--gpu-markers"); }
        else if (std::wcscmp(argv[i], L"--dxc") == 0) { g_options.dxc = true; g_flags.push_back("--dxc"); }
        else if (std::wcscmp(argv[i], L"--mesh") == 0) { g_options.mesh = g_options.dxc = true; g_flags.push_back("--mesh"); }
        else if (std::wcscmp(argv[i], L"--msaa") == 0 && i + 1 < argc)
        {
            long value = 0;
            if (!ParseBounded(argv[++i], 1, 8, value) || (value & (value - 1)) != 0)
            {
                std::fprintf(stderr, "--msaa must be 1, 2, 4 or 8.\n");
                return 2;
            }
            g_options.msaa = static_cast<UINT>(value);
            g_flags.push_back("--msaa " + std::to_string(value));
        }
        else if (std::wcscmp(argv[i], L"--mrt") == 0 && i + 1 < argc)
        {
            long value = 0;
            if (!ParseBounded(argv[++i], 1, 2, value))
            {
                std::fprintf(stderr, "--mrt must be 1 or 2.\n");
                return 2;
            }
            g_options.mrt = static_cast<UINT>(value);
            g_flags.push_back("--mrt " + std::to_string(value));
        }
        else if (std::wcscmp(argv[i], L"--programmatic-capture") == 0 && i + 1 < argc)
        {
            g_options.programmaticCapture = argv[++i];
            g_flags.push_back("--programmatic-capture");
        }
        else if ((std::wcscmp(argv[i], L"--capture-at") == 0 || std::wcscmp(argv[i], L"--capture-frames") == 0) && i + 1 < argc)
        {
            bool at = std::wcscmp(argv[i], L"--capture-at") == 0;
            long value = 0;
            if (!ParseBounded(argv[++i], 1, at ? 100000 : 10, value))
            {
                std::fprintf(stderr, at ? "--capture-at must be between 1 and 100000.\n" : "--capture-frames must be between 1 and 10.\n");
                return 2;
            }
            (at ? g_options.captureAt : g_options.captureFrames) = static_cast<int>(value);
            g_flags.push_back(std::string(at ? "--capture-at " : "--capture-frames ") + std::to_string(value));
        }
        else if (std::wcscmp(argv[i], L"--report") == 0 && i + 1 < argc) g_options.reportPath = argv[++i];
        else if (std::wcscmp(argv[i], L"--workload") == 0 && i + 1 < argc)
        {
            if (std::wcscmp(argv[++i], L"perf") != 0)
            {
                std::fprintf(stderr, "--workload must be perf.\n");
                return 2;
            }
            g_options.perf = g_options.depth = g_options.indirect = g_options.asyncOverlap = g_options.gpuMarkers = true;
            g_flags.push_back("--workload perf");
        }
        else
        {
            std::fwprintf(stderr, L"Unknown or incomplete argument: %ls\n", argv[i]);
            return 2;
        }
    }

    WNDCLASSW wc = {};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"D3D12TestAppWindow";
    RegisterClassW(&wc);
    RECT rect = { 0, 0, static_cast<LONG>(kWidth), static_cast<LONG>(kHeight) };
    AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
    HWND hwnd = CreateWindowW(wc.lpszClassName, L"pixmcp D3D12 test app", WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
        rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, wc.hInstance, nullptr);
    if (!hidden) ShowWindow(hwnd, SW_SHOW);

    if (startupDelayMs > 0) Sleep(static_cast<DWORD>(startupDelayMs));
    Init(hwnd);
    WriteReport();
    std::printf("Variant: %s; graphics b1 DWORDs: 0x50495831, %u, 0xffffffff, 0x3f800000; compute b0: %s.\n",
        g_candidate ? "candidate" : "baseline", g_candidate ? 2u : 1u, g_candidate ? "0x3f400000" : "0x3f000000");
    std::printf("Rendering%s%s...\n", maxFrames < 0 ? "" : " (limited frames)", hang ? " and hanging the GPU soon" : "");

    UINT frame = 0;
    MSG msg = {};
    while (!g_quit && (maxFrames < 0 || static_cast<int>(frame) < maxFrames))
    {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        if (g_quit) break;
        if (!g_options.programmaticCapture.empty() && static_cast<int>(frame) == g_options.captureAt)
        {
            // Needs the PIX GPU capturer in the process (pix_device_launch with underGpuCapture).
            HRESULT hr = PIXGpuCaptureNextFrames(g_options.programmaticCapture.c_str(), static_cast<UINT32>(g_options.captureFrames));
            char text[96];
            std::snprintf(text, sizeof(text), "PIXGpuCaptureNextFrames hr=0x%08X at frame %d for %d frame(s)", static_cast<unsigned>(hr), g_options.captureAt, g_options.captureFrames);
            g_captureResult = text;
            std::printf("%s\n", text);
            std::fflush(stdout);
            WriteReport();
        }
        bool hangNow = hang && static_cast<int>(frame) == hangAfter;
        if (timingWorkload)
        {
            PIXBeginEvent(PIX_COLOR(40, 100, 200), L"Fixture Frame");
            PIXReportCounter(L"Fixture Frame Number", static_cast<float>(frame));
            FixtureTimingOuter();
        }
        if (hangNow) std::printf("Submitting a never-terminating dispatch to provoke a GPU timeout...\n");
        RunCompute(frame, hangNow);
        if (g_options.perf) RenderPerf(frame++);
        else Render(frame++);
        if (timingWorkload) PIXEndEvent();
        if (hangNow)
        {
            // The runtime reports the device removal on the next fence wait or Present; make that happen now.
            WaitForCompute();
            Check(g_device->GetDeviceRemovedReason(), "GetDeviceRemovedReason");
        }
    }
    WaitForCompute();
    WaitForGpu();
    if (g_mappedConstants) g_constantBuffer->Unmap(0, nullptr);
    CloseHandle(g_fenceEvent);
    CloseHandle(g_computeFenceEvent);
    std::printf("Rendered %u frames.\n", frame);
    return 0;
}
