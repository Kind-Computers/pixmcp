// Minimal D3D12 test application used to exercise pixmcp end to end. Each frame renders three
// triangles (one indexed, one textured through a descriptor table) with nested PIX markers on the
// graphics queue and runs a small compute dispatch on a separate compute queue, so a GPU capture has
// draws, a dispatch, two queues, a PSO, a root signature with a descriptor table, vertex/index/
// constant buffers, a texture SRV, a UAV and per-event timing.
//
// Usage: D3D12TestApp.exe [--frames N] [--variant baseline|candidate] [--duplicate-markers] [--hidden] [--hang [--hang-after N]]
//   --frames N      stop after N frames (default: run until the window is closed)
//   --variant       deterministic baseline (default) or candidate: altered shader, an extra pass,
//                   larger compute resource, and distinct root constants; marker paths remain stable.
//   --duplicate-markers  emit a second "Triangle pass" to test ambiguous marker matching
//   --hidden        keep the test window hidden for automated capture runs
//   --startup-delay-ms N  wait before creating the D3D12 device (readiness regression fixture)
//   --timing-workload     emit named CPU PIX events/counter and spend CPU time in known noinline functions
//   --hang          submit a never-terminating compute shader after --hang-after frames (default 30)
//                   to provoke a GPU timeout / device removal, which produces a DirectX dump file when
//                   DRED and dump-file retention are enabled (see pix_device_d3d_settings_set).
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
#include <pix3.h>

#pragma comment(lib, "d3d12.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "user32.lib")

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
    volatile double g_timingSink = 0;

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
    void BeginEvent(ID3D12GraphicsCommandList* list, const wchar_t* name)
    {
        list->BeginEvent(0, name, static_cast<UINT>((std::wcslen(name) + 1) * sizeof(wchar_t)));
    }
    void EndEvent(ID3D12GraphicsCommandList* list) { list->EndEvent(); }
    void SetMarker(ID3D12GraphicsCommandList* list, const wchar_t* name)
    {
        list->SetMarker(0, name, static_cast<UINT>((std::wcslen(name) + 1) * sizeof(wchar_t)));
    }

    void SetName(ID3D12Object* object, const wchar_t* name) { object->SetName(name); }

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

    ComPtr<ID3DBlob> CompileShader(const char* source, const char* entry, const char* target)
    {
        ComPtr<ID3DBlob> blob, error;
        UINT compileFlags = D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
        if (FAILED(D3DCompile(source, std::strlen(source), "triangle.hlsl", nullptr, nullptr, entry, target, compileFlags, 0, &blob, &error)))
        {
            std::fprintf(stderr, "%s compile: %s\n", entry, error ? static_cast<const char*>(error->GetBufferPointer()) : "?");
            std::exit(1);
        }
        return blob;
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
        for (UINT i = 0; SUCCEEDED(factory->EnumAdapterByGpuPreference(i, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter))); ++i)
        {
            DXGI_ADAPTER_DESC1 desc;
            adapter->GetDesc1(&desc);
            if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            if (SUCCEEDED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&g_device)))) { std::wprintf(L"Adapter: %s\n", desc.Description); break; }
        }
        if (!g_device) Check(E_FAIL, "No D3D12 adapter");
        SetName(g_device.Get(), L"D3D12TestApp Device");

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
        scDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        scDesc.SampleDesc.Count = 1;
        ComPtr<IDXGISwapChain1> swapChain1;
        Check(factory->CreateSwapChainForHwnd(g_queue.Get(), hwnd, &scDesc, nullptr, nullptr, &swapChain1), "CreateSwapChainForHwnd");
        Check(swapChain1.As(&g_swapChain), "SwapChain QI");
        g_frameIndex = g_swapChain->GetCurrentBackBufferIndex();

        D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc = {};
        rtvHeapDesc.NumDescriptors = kFrameCount;
        rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        Check(g_device->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&g_rtvHeap)), "CreateDescriptorHeap");
        SetName(g_rtvHeap.Get(), L"RTV Heap");
        g_rtvStride = g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

        D3D12_DESCRIPTOR_HEAP_DESC srvHeapDesc = {};
        srvHeapDesc.NumDescriptors = 1;
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

        ComPtr<ID3DBlob> vs = CompileShader(kShader, "VSMain", "vs_5_0");
        ComPtr<ID3DBlob> ps = CompileShader(kShader, g_candidate ? "PSMainCandidate" : "PSMain", "ps_5_0");
        ComPtr<ID3DBlob> cs = CompileShader(kComputeShader, "CSMain", "cs_5_0");
        ComPtr<ID3DBlob> csHang = CompileShader(kComputeShader, "CSHang", "cs_5_0");

        D3D12_INPUT_ELEMENT_DESC layout[] =
        {
            { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
            { "COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
            { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 28, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
        };
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pso = {};
        pso.InputLayout = { layout, 3 };
        pso.pRootSignature = g_rootSignature.Get();
        pso.VS = { vs->GetBufferPointer(), vs->GetBufferSize() };
        pso.PS = { ps->GetBufferPointer(), ps->GetBufferSize() };
        pso.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
        pso.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
        pso.RasterizerState.DepthClipEnable = TRUE;
        pso.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        pso.SampleMask = UINT_MAX;
        pso.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        pso.NumRenderTargets = 1;
        pso.RTVFormats[0] = DXGI_FORMAT_R8G8B8A8_UNORM;
        pso.SampleDesc.Count = 1;
        Check(g_device->CreateGraphicsPipelineState(&pso, IID_PPV_ARGS(&g_pipelineState)), "CreateGraphicsPipelineState");
        SetName(g_pipelineState.Get(), L"Triangle PSO");

        D3D12_COMPUTE_PIPELINE_STATE_DESC cpso = {};
        cpso.pRootSignature = g_computeRootSignature.Get();
        cpso.CS = { cs->GetBufferPointer(), cs->GetBufferSize() };
        Check(g_device->CreateComputePipelineState(&cpso, IID_PPV_ARGS(&g_computeState)), "CreateComputePipelineState");
        SetName(g_computeState.Get(), L"Wave PSO");
        cpso.CS = { csHang->GetBufferPointer(), csHang->GetBufferSize() };
        Check(g_device->CreateComputePipelineState(&cpso, IID_PPV_ARGS(&g_hangState)), "CreateComputePipelineState (hang)");
        SetName(g_hangState.Get(), L"Hang PSO");

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
        Check(g_computeList->Reset(g_computeAllocator.Get(), hang ? g_hangState.Get() : g_computeState.Get()), "Compute list Reset");
        BeginEvent(g_computeList.Get(), hang ? L"Hang dispatch" : L"Wave compute");
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

        D3D12_CPU_DESCRIPTOR_HANDLE rtv = g_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        rtv.ptr += static_cast<SIZE_T>(g_frameIndex) * g_rtvStride;
        g_commandList->OMSetRenderTargets(1, &rtv, FALSE, nullptr);

        BeginEvent(g_commandList.Get(), L"Clear");
        const float clear[] = { 0.05f, 0.05f, 0.15f, 1.0f };
        g_commandList->ClearRenderTargetView(rtv, clear, 0, nullptr);
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

        barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        g_commandList->ResourceBarrier(1, &barrier);
        EndEvent(g_commandList.Get());

        Check(g_commandList->Close(), "Close");
        ID3D12CommandList* lists[] = { g_commandList.Get() };
        g_queue->ExecuteCommandLists(1, lists);
        Check(g_swapChain->Present(1, 0), "Present");
        MoveToNextFrame();
    }
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
        bool hangNow = hang && static_cast<int>(frame) == hangAfter;
        if (timingWorkload)
        {
            PIXBeginEvent(PIX_COLOR(40, 100, 200), L"Fixture Frame");
            PIXReportCounter(L"Fixture Frame Number", static_cast<float>(frame));
            FixtureTimingOuter();
        }
        if (hangNow) std::printf("Submitting a never-terminating dispatch to provoke a GPU timeout...\n");
        RunCompute(frame, hangNow);
        Render(frame++);
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
