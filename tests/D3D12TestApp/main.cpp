// Minimal D3D12 test application used to exercise pixmcp end to end: it renders a spinning
// triangle with a few PIX-visible markers, so a GPU capture of it has draws, a PSO, a root
// signature, vertex/constant buffers and per-event timing.
//
// Usage: D3D12TestApp.exe [--frames N]   (default: run until the window is closed)
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

    struct Vertex { float position[3]; float color[4]; };
    struct Constants { float angle; float aspect; float pad[62]; }; // 256-byte aligned CBV

    ComPtr<ID3D12Device> g_device;
    ComPtr<ID3D12CommandQueue> g_queue;
    ComPtr<IDXGISwapChain3> g_swapChain;
    ComPtr<ID3D12DescriptorHeap> g_rtvHeap;
    ComPtr<ID3D12Resource> g_renderTargets[kFrameCount];
    ComPtr<ID3D12CommandAllocator> g_allocators[kFrameCount];
    ComPtr<ID3D12GraphicsCommandList> g_commandList;
    ComPtr<ID3D12RootSignature> g_rootSignature;
    ComPtr<ID3D12PipelineState> g_pipelineState;
    ComPtr<ID3D12Resource> g_vertexBuffer;
    ComPtr<ID3D12Resource> g_constantBuffer;
    ComPtr<ID3D12Fence> g_fence;
    HANDLE g_fenceEvent = nullptr;
    UINT64 g_fenceValues[kFrameCount] = {};
    UINT g_frameIndex = 0;
    UINT g_rtvStride = 0;
    D3D12_VERTEX_BUFFER_VIEW g_vbv = {};
    Constants* g_mappedConstants = nullptr;
    bool g_quit = false;

    void Check(HRESULT hr, const char* what)
    {
        if (FAILED(hr))
        {
            std::fprintf(stderr, "%s failed: 0x%08X\n", what, static_cast<unsigned>(hr));
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
struct VSIn  { float3 pos : POSITION; float4 color : COLOR; };
struct PSIn  { float4 pos : SV_POSITION; float4 color : COLOR; };
PSIn VSMain(VSIn input)
{
    float c = cos(angle), s = sin(angle);
    float2 p = float2(input.pos.x * c - input.pos.y * s, input.pos.x * s + input.pos.y * c);
    PSIn o;
    o.pos = float4(p.x / aspect, p.y, input.pos.z, 1.0);
    o.color = input.color;
    return o;
}
float4 PSMain(PSIn input) : SV_TARGET { return input.color; }
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

    ComPtr<ID3D12Resource> CreateUploadBuffer(UINT64 size, const wchar_t* name)
    {
        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC desc = {};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        desc.Width = size;
        desc.Height = 1;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.SampleDesc.Count = 1;
        desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ComPtr<ID3D12Resource> buffer;
        Check(g_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&buffer)), "CreateCommittedResource");
        SetName(buffer.Get(), name);
        return buffer;
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
        g_rtvStride = g_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

        D3D12_CPU_DESCRIPTOR_HANDLE rtv = g_rtvHeap->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < kFrameCount; ++i)
        {
            Check(g_swapChain->GetBuffer(i, IID_PPV_ARGS(&g_renderTargets[i])), "GetBuffer");
            g_device->CreateRenderTargetView(g_renderTargets[i].Get(), nullptr, rtv);
            SetName(g_renderTargets[i].Get(), i == 0 ? L"BackBuffer 0" : L"BackBuffer 1");
            rtv.ptr += g_rtvStride;
            Check(g_device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&g_allocators[i])), "CreateCommandAllocator");
        }

        D3D12_ROOT_PARAMETER param = {};
        param.ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        param.Descriptor.ShaderRegister = 0;
        param.ShaderVisibility = D3D12_SHADER_VISIBILITY_VERTEX;
        D3D12_ROOT_SIGNATURE_DESC rsDesc = {};
        rsDesc.NumParameters = 1;
        rsDesc.pParameters = &param;
        rsDesc.Flags = D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT;
        ComPtr<ID3DBlob> signature, error;
        Check(D3D12SerializeRootSignature(&rsDesc, D3D_ROOT_SIGNATURE_VERSION_1, &signature, &error), "D3D12SerializeRootSignature");
        Check(g_device->CreateRootSignature(0, signature->GetBufferPointer(), signature->GetBufferSize(), IID_PPV_ARGS(&g_rootSignature)), "CreateRootSignature");
        SetName(g_rootSignature.Get(), L"Triangle Root Signature");

        ComPtr<ID3DBlob> vs, ps;
        UINT compileFlags = D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
        if (FAILED(D3DCompile(kShader, std::strlen(kShader), "triangle.hlsl", nullptr, nullptr, "VSMain", "vs_5_0", compileFlags, 0, &vs, &error)))
        {
            std::fprintf(stderr, "VS compile: %s\n", error ? static_cast<const char*>(error->GetBufferPointer()) : "?");
            std::exit(1);
        }
        if (FAILED(D3DCompile(kShader, std::strlen(kShader), "triangle.hlsl", nullptr, nullptr, "PSMain", "ps_5_0", compileFlags, 0, &ps, &error)))
        {
            std::fprintf(stderr, "PS compile: %s\n", error ? static_cast<const char*>(error->GetBufferPointer()) : "?");
            std::exit(1);
        }

        D3D12_INPUT_ELEMENT_DESC layout[] =
        {
            { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
            { "COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 12, D3D12_INPUT_CLASSIFICATION_PER_VERTEX_DATA, 0 },
        };
        D3D12_GRAPHICS_PIPELINE_STATE_DESC pso = {};
        pso.InputLayout = { layout, 2 };
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

        Check(g_device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_allocators[g_frameIndex].Get(), g_pipelineState.Get(), IID_PPV_ARGS(&g_commandList)), "CreateCommandList");
        SetName(g_commandList.Get(), L"Frame Command List");
        Check(g_commandList->Close(), "Close");

        Vertex vertices[] =
        {
            { { 0.0f, 0.5f, 0.0f }, { 1.0f, 0.0f, 0.0f, 1.0f } },
            { { 0.5f, -0.5f, 0.0f }, { 0.0f, 1.0f, 0.0f, 1.0f } },
            { { -0.5f, -0.5f, 0.0f }, { 0.0f, 0.0f, 1.0f, 1.0f } },
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

        g_constantBuffer = CreateUploadBuffer(sizeof(Constants) * kFrameCount, L"Per-frame Constants");
        Check(g_constantBuffer->Map(0, &noRead, reinterpret_cast<void**>(&g_mappedConstants)), "Map CB");

        Check(g_device->CreateFence(g_fenceValues[g_frameIndex], D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_fence)), "CreateFence");
        SetName(g_fence.Get(), L"Frame Fence");
        g_fenceValues[g_frameIndex]++;
        g_fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        WaitForGpu();
    }

    void Render(UINT frame)
    {
        Constants& c = g_mappedConstants[g_frameIndex];
        c.angle = frame * 0.02f;
        c.aspect = static_cast<float>(kWidth) / kHeight;

        Check(g_allocators[g_frameIndex]->Reset(), "Allocator Reset");
        Check(g_commandList->Reset(g_allocators[g_frameIndex].Get(), g_pipelineState.Get()), "CommandList Reset");

        wchar_t frameName[64];
        swprintf_s(frameName, L"Frame %u", frame);
        BeginEvent(g_commandList.Get(), frameName);

        g_commandList->SetGraphicsRootSignature(g_rootSignature.Get());
        g_commandList->SetGraphicsRootConstantBufferView(0, g_constantBuffer->GetGPUVirtualAddress() + g_frameIndex * sizeof(Constants));
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
        for (UINT i = 0; i < 3; ++i)
        {
            g_commandList->DrawInstanced(3, 1, 0, 0);
        }
        EndEvent(g_commandList.Get());

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
    for (int i = 1; i < argc; ++i)
    {
        if (std::wcscmp(argv[i], L"--frames") == 0 && i + 1 < argc) maxFrames = _wtoi(argv[++i]);
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
    ShowWindow(hwnd, SW_SHOW);

    Init(hwnd);
    std::printf("Rendering%s...\n", maxFrames < 0 ? "" : " (limited frames)");

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
        Render(frame++);
    }
    WaitForGpu();
    if (g_mappedConstants) g_constantBuffer->Unmap(0, nullptr);
    CloseHandle(g_fenceEvent);
    std::printf("Rendered %u frames.\n", frame);
    return 0;
}
