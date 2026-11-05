# XR AI Edge

On-device generative AI in mixed reality on **Meta Quest 3 / 3S**. This is a spatial port of three experiences from
[Google AI Edge Gallery](https://github.com/google-ai-edge/gallery): **AI Chat**, **Ask Image** and **Tiny Garden**.
Each one is rebuilt for passthrough, hand tracking and the Meta Depth API. Everything runs on the headset: the
language models, the image questions and the voice. Nothing is sent to a server.

| Experience | What it does in MR |
|---|---|
| **Spatial Chat** | Multi-turn chat with Gemma 4 E2B on a world-locked panel. Replies stream in and are read aloud. Past conversations are cards you can grab to resume. |
| **Point & Ask** | Frame something in the real world with both hands (or point at it) and ask about it. The crop from the passthrough camera goes to Gemma 4 E2B with the question. The answer is pinned in space next to the object. Depth adds real measurements to the prompt ("about 34 cm wide, 1.1 m away"), so you can ask about size and fit. |
| **Tabletop Garden** | A 3×3 planter placed on a real table with depth. You garden by voice or keyboard through the Gallery's FunctionGemma 270M Tiny Garden model (plant, water, harvest). You can also point ("water *these*") or play by hand with a watering can, seed packets, and pulling flowers. |

The project is built on the open-source on-device AI stack from [Google AI Edge](https://github.com/google-ai-edge):
the Gallery app's experiences, the LiteRT-LM runtime, and the LiteRT-LM models published for it.

Replies in all three experiences can be spoken by neural voices (Piper, through sherpa-onnx), in **English** and
**Italian**. Each reply is read by the voice of its language, detected from the text. Ask in Italian and Gemma answers
in Italian, read by the Italian voice. The system text-to-speech engine on Quest is disabled.

## Requirements

- **Headset:** Meta Quest 3 or 3S. Passthrough Camera Access and depth need the Quest 3 family.
- **Unity:** 6000.6.7f1 with Android Build Support (OpenJDK, SDK and NDK).
- **Git LFS:** binaries (images, models, audio, video, native libraries) are stored in LFS. Install it before cloning:

  ```bash
  git lfs install
  ```

- **Storage on the headset:** about 3.1 GB for the models and both voices.
- **Hugging Face token:** only needed for Tiny Garden (see below).

## Getting started

1. Clone the repository with Git LFS installed, then open it in Unity 6000.6.7f1. The project is built on Unity's
   Mixed Reality template: XR Origin + XR Interaction Toolkit, AR Foundation, OpenXR with Meta features, and URP.
2. Switch the build target to **Android**. The only build scene is `Assets/Scenes/SampleScene.unity`.
3. Build and install, either from **File > Build And Run** or in batch mode (close the Editor first):

   ```bash
   "<Unity.exe>" -batchmode -quit -projectPath . -buildTarget Android -logFile -
   ```

4. On the headset, allow camera and spatial data access when asked. The **launcher** panel opens in front of you.
   From it you download the models and the voices, then open an experience.

### Models and voices

Downloads go to the app's storage (`/sdcard/Android/data/<package>/files/`) and are pinned to fixed Hugging Face
commits. They resume after interruption and can be deleted from the launcher.

| Item | Used by | Size | Access |
|---|---|---|---|
| Gemma 4 E2B-it (`litert-community/gemma-4-E2B-it-litert-lm`) | Chat, Point & Ask | 2.6 GB | Open |
| FunctionGemma 270M Tiny Garden (`litert-community/functiongemma-270m-ft-tiny-garden`) | Tabletop Garden | 289 MB | Gated (auto-approve) |
| Piper "alba" voice (`csukuangfj/vits-piper-en_GB-alba-medium`) | Spoken replies in English | 81 MB | Open |
| Piper "paola" voice (`csukuangfj/vits-piper-it_IT-paola-medium`) | Spoken replies in Italian | 82 MB | Open |

For the gated Tiny Garden model:

1. Accept its license on Hugging Face.
2. Create a read token.
3. Put the token in a file named `hf_token.txt` in the app's files folder, for example:

   ```bash
   metavr files push hf_token.txt /sdcard/Android/data/com.DefaultCompany.MixedRealityTemplate/files/hf_token.txt
   ```

The token stays on the device. **Never commit it.**

## Project layout

```
Assets/AIEdge/                 All port code (asmdef AIEdge, namespace AIEdge)
  Core/                        LLM engine and service, model catalog/downloader/store, speech engine, voice pack, language detection
  Camera/                      Passthrough camera capture and crops
  Spatial/                     Depth service, depth-based measurement and placement
  Experiences/Chat|AskImage|TinyGarden/
  UI/                          Launcher, UI factory, Markdown → TextMeshPro, voice toggle
  Diagnostics/SelfTest.cs      On-device self-tests
  Tests/Editor/                EditMode unit tests
Assets/Plugins/Android/LiteRtBridge.androidlib/
                               Kotlin bridge: LiteRT-LM (LlmBridge), system TTS (SpeechBridge),
                               neural TTS (NeuralSpeechBridge), sherpa-onnx jar + arm64 native library
Assets/MRTemplateAssets/       Unity MR template code and assets
```

Notes:

- **Bridge:** the Kotlin bridge is a Gradle library module that Unity builds with the app. LiteRT-LM
  (`com.google.ai.edge.litertlm:litertlm-android:0.18.1`) comes from Maven. Its manifest entries are merged by Gradle,
  so the SDK-generated `Assets/Plugins/Android/AndroidManifest.xml` is never edited by hand.
- **Rig:** the rig stays the template's XR Origin. Depth uses MRUK's `EnvironmentRaycastManager`, and image input uses
  Passthrough Camera Access.

## Testing

**EditMode unit tests** (text processing, chat history, garden rules, depth math, catalog, downloads):

```bash
"<Unity.exe>" -batchmode -projectPath . -runTests -testPlatform EditMode -testFilter AIEdge -testResults results.xml -logFile -
```

**On-device self-tests:**

1. Push a `selftest.json` to the app's files folder and launch the app. For example, `{"tests":["chat_multiturn","speech"]}` runs only the named tests; any other content runs all of them.
2. The available tests are:
   - Downloads: `resume_download`, `small_download`, `gated_without_token`, `cancel_and_reset`, `unload_memory`
   - Chat and Point & Ask: `chat_multiturn`, `ask_image`
   - Garden: `garden`, `garden_view`
   - Depth: `depth`, `depth_ask`, `depth_place`, `depth_garden`
   - Voice: `speech`, `speech_it`
3. Results are logged as `SELFTEST|{json}` lines (logcat tag `Unity`) and written to `selftest_results.txt` in the same folder.
4. Delete `selftest.json` afterwards, or the tests run on every launch.

## Credits and licenses

Thanks to the [Google AI Edge](https://github.com/google-ai-edge) team. This port would not exist without their
open-source Gallery app and LiteRT-LM runtime.

This repository contains only original code and Unity template assets. The models and the voices are downloaded at
runtime under their own terms:

| Component | License / terms |
|---|---|
| [Google AI Edge Gallery](https://github.com/google-ai-edge/gallery): experience design, Tiny Garden system prompt and tool schema | Apache 2.0 |
| [LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) runtime | Apache 2.0 |
| Gemma 4 E2B and FunctionGemma 270M Tiny Garden models | [Gemma Terms of Use](https://ai.google.dev/gemma/terms) |
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) v1.13.8 (bundled jar and `libsherpa-onnx-jni.so`, includes ONNX Runtime, MIT) | Apache 2.0 |
| [Piper](https://github.com/rhasspy/piper) "alba" voice: training data from the [University of Edinburgh](https://datashare.ed.ac.uk/handle/10283/3270) | CC BY 4.0 |
| [Piper](https://github.com/rhasspy/piper) "paola" voice: training data from [paolapersico1/Voice-Dataset-Italian](https://huggingface.co/datasets/paolapersico1/Voice-Dataset-Italian) | CC0 1.0 |
| espeak-ng data, downloaded with each voice for phonemization | GPL-3.0 |
| Unity Mixed Reality template and package samples | Unity Companion License |

Tiny Garden's original game art and code are not used. The garden is rebuilt in Unity from primitives.
