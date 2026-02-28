# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Windows-native port of OpenAI's Whisper ASR model, derived from whisper.cpp. Uses **DirectCompute (D3D 11.0)** for GPU inference instead of CUDA/GGML. Output is `Whisper.dll` (native C++) with C#/.NET and PowerShell wrappers.

**Platform:** 64-bit Windows only (8.1+), requires D3D 11.0 GPU, AVX1 + F16C CPU.

## Build System

**Toolchain:** Visual Studio 2022 (v17.4+), MSVC C++17, C# .NET 6 / .NET Framework 4.8.

**Solution:** `WhisperCpp.sln` — only `Release|x64` and `Debug|x64` configurations exist.

**Build order (strict dependency chain):**

1. Build and run `Tools/CompressShaders` — compiles all 46 HLSL compute shaders to DXBC, LZ4-compresses them into a binary blob embedded in Whisper.dll. Must succeed with output like: `Compressed 46 compute shaders, 123.5 kb -> 18.0 kb`
2. Build `Whisper` project — produces `Whisper.dll` (native C++ DLL, statically linked CRT by default with `/MT`)
3. Optionally build `WhisperNet` (C# NuGet), `WhisperPS` (PowerShell module), or example apps

There is no command-line build script — the project is built entirely through the Visual Studio solution.

## Testing

There is **no automated test suite or test runner**. Testing is manual/developer-only:

- `Whisper/ML/tensorOpsTests.cpp` — GPU operation correctness tests (disabled by default via early `return;` statements; uncomment to enable)
- `Whisper/ML/testUtils.h/cpp` — `computeDiff()` for comparing GPU vs CPU results
- `Tools/compareTraces/` — binary trace comparison for numerical validation
- `BUILD_BOTH_VERSIONS` preprocessor flag enables the original whisper.cpp reference CPU path for side-by-side comparison

## Architecture

### COM-Lite Interface Layer

Custom lightweight COM-style interface system (not full COM/DCOM), defined in `ComLightLib/`:
- Interfaces inherit `IUnknown` with `__declspec(uuid(...))` GUIDs
- Server objects use `ComLight::ObjectRoot<T>` / `ComLight::Object<T>`
- Client code uses `ComLight::CComPtr<T>` smart pointers
- C# interop uses the `ComLightInterop` NuGet package

### Core Interfaces (in `Whisper/API/`)

| Interface | Purpose |
|---|---|
| `iModel` | Loaded model — `createContext()`, `tokenize()`, `getSpecialTokens()` |
| `iContext` | Inference context — `runFull()`, `runStreamed()`, `runCapture()`, `getResults()` |
| `iMediaFoundation` | Audio I/O factory — `loadAudioFile()`, `openCaptureDevice()` |
| `iAudioBuffer` | In-memory PCM (mono float32 @ 16kHz + optional stereo) |
| `iAudioReader` | Streaming audio from MF source reader |
| `iAudioCapture` | Real-time microphone capture |
| `iTranscribeResult` | Output segments + tokens |

### Inference Pipeline

```
loadModel(path) → iModel (WhisperModel reads GGML binary, uploads weights to VRAM)
  → createContext() → iContext (ContextImpl)

Audio input (via Media Foundation) → iAudioBuffer or iAudioReader

runFull/runStreamed:
  PCM → MEL spectrogram (FFT on CPU, multi-threaded)
  For each 30s chunk:
    encode(mel) → N encoder layers via HLSL compute shaders
    decode loop → N decoder layers, greedy token sampling until EOT
  → iTranscribeResult (segments with timestamps + tokens)
```

### GPU Tensor System (`Whisper/ML/`)

- `Tensor` — 48-byte GPU tensor: `TensorShape` (ne[4]/nb[4]) + D3D11 SRV/UAV views
- `MlContext` — GPU ML operations dispatching HLSL compute shaders: `mulMat`, `flashAttention`, `convolution`, `norm`, `softMax`, `addRepeat`, `diagMaskInf`, `addRows`
- `WhisperContext` (extends `MlContext`) — model execution with pooled tensor arenas and KV cache
- `ModelBuffers` — GPU-resident weights structured as `EncoderBuffers` + `DecoderBuffers` with per-layer `TensorPair` (weights + biases)
- `TensorsArena` — GPU memory pool reusing D3D11 buffer allocations

### HLSL Compute Shaders (`ComputeShaders/`)

46 shaders, compiled offline to DXBC, LZ4-compressed into DLL. Key shaders:
- `mulMatTiled.hlsl` / `mulMatByRowTiled.hlsl` — matrix multiply (most expensive, heavily tuned)
- `flashAttention.hlsl` — flash attention
- `*64.hlsl` variants — for AMD GPUs (different wave size)
- FP64 shaders detected at load time and skipped if hardware lacks support

### Three Model Implementations (compile-time)

1. **GPU** (default): Full DirectCompute encoder + decoder
2. **Hybrid** (`BUILD_HYBRID_VERSION`): GPU encoder + AVX2-optimized CPU decoder (`Whisper/CPU/`, `Whisper/Hybrid/`)
3. **Reference** (`BUILD_BOTH_VERSIONS`): Original whisper.cpp CPU code (`Whisper/source/`) for validation

### MEL Spectrogram

- Audio: 16 kHz, 400-point FFT, 160-sample hop (10 ms), 80 MEL bins
- Buffered mode: `Whisper/Whisper/Spectrogram.h` — full audio upfront, multi-threaded via `parallelFor`
- Streaming mode: `Whisper/Whisper/MelStreamer.h` — background thread pre-computes chunks

### Key Subsystems

- **Voice Activity Detection** (`Whisper/Whisper/voiceActivityDetection.h`): Moattar & Homayoonpoor 2009 algorithm (energy + dominant frequency + spectral flatness)
- **Speaker Diarization** (`ContextImpl.diarize.cpp`): L/R stereo channel energy comparison
- **Profiling** (`Utils/GpuProfiler.h`, `Utils/CpuProfiler.h`): RAII-scoped GPU (D3D11 timestamp queries) and CPU (TSC) timing
- **RenderDoc integration**: Hold F12 to capture compute calls when launched from RenderDoc; debug builds include debug shaders

## Key Conventions

- Runtime library is statically linked (`/MT`) by default so Whisper.dll has no VC++ redistributable dependency. Switch to `/MD` if distributing CRT separately.
- Development-only code (alternative implementations, FP64 shaders, debug tracing) is disabled via preprocessor macros or `constexpr` flags and kept in the repo.
- The `Whisper/source/` directory contains the original unmodified whisper.cpp/ggml code for reference — do not modify it.
- Models are GGML binary format, downloaded from HuggingFace. Supported sizes: tiny, base, small, medium, large.
