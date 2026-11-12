# XR AI Edge

On-device generative AI in mixed reality on **Meta Quest 3 / 3S**. This is a spatial port of three experiences from
[Google AI Edge Gallery](https://github.com/google-ai-edge/gallery): **AI Chat**, **Ask Image** and **Tiny Garden**.
Each one is rebuilt for passthrough, hand tracking and the Meta Depth API, and a fourth experience, **What's That?**, is
new. Everything runs on the headset: the language models, the image questions, the voices and speech recognition.
Nothing is sent to a server. The app speaks and listens in **English** or **Italian**.

| Experience | What it does in MR |
|---|---|
| **Spatial Chat** | Multi-turn chat with Gemma 4 E2B on a world-locked panel. Type or press **Talk** and speak. Replies stream in and are read aloud. Past conversations are cards you can grab to resume. |
| **Point & Ask** | Frame something in the real world with both hands (or point at it) and ask about it, typed or spoken. The crop from the passthrough camera goes to Gemma 4 E2B with the question. The answer is pinned in space next to the object. Depth adds real measurements to the prompt ("about 34 cm wide, 1.1 m away"), so you can ask about size and fit. |
| **Tabletop Garden** | A 3×3 planter placed on a real table with depth. You garden by speaking (**Talk**) or typing through the Gallery's FunctionGemma 270M Tiny Garden model (plant, water, harvest). The model was trained on English commands, so English works best. You can also point ("water *these*") or play by hand with a watering can, seed packets, and pulling flowers. |
| **What's That?** | Point at something in the room, with your index finger (hold still for a moment) or with a controller (pull the trigger). A ray and a reticle show where depth finds a real surface, with the live distance. Gemma names the object and describes it briefly, in the language set in the main menu. The card pinned beside the object and the spoken reply give its distance from you, measured by depth rather than guessed. The object is highlighted: a live depth outline follows your pointing, and on selection an on-device interactive segmentation model (MediaPipe's magic touch, run with LiteRT in about 0.2 s) cuts it out of the camera image. While the panel is open, people in view are found in the background (EfficientDet-Lite0 with LiteRT, about 4–5 frames a second): each one gets a frame in the room with their distance from depth ("Person · 2.3 m"), and the panel sums them up. Only boxes are found, nothing that identifies anyone. The People button turns it off. |

The project is built on the open-source on-device AI stack from [Google AI Edge](https://github.com/google-ai-edge):
the Gallery app's experiences, the LiteRT-LM runtime, and the LiteRT-LM models published for it.

### Language, voices and speech input

**Language.** The **English** / **Italiano** buttons at the top right of the main menu set one language for the whole
app, and the choice is remembered between sessions. It sets:

- the language of every model reply, in every experience;
- the voice that reads the reply aloud;
- the language speech input listens for.

Each conversation's system prompt ends with a rule to reply only in that language, so replies stay in it even when a
chat persona, your instructions or a requested style (a pirate, say) are written in another. In Italian, a short
reminder is also added to each message sent to the model; the conversation you see keeps your own words. The voice is
fixed to the chosen language for the whole reply, so it never switches partway through.

**Voices.** Replies are read by neural voices (Piper, through sherpa-onnx): "alba" for English and "paola" for Italian.
Download them in the main menu; **Test voice** plays a sample. **Voice on / Voice off** on every panel mutes or unmutes
replies for the whole app. The system text-to-speech engine on Quest is disabled.

**Speech input.** Press **Talk** in Chat, Point & Ask or Tabletop Garden, speak, and press **Stop**. Whisper base
(int8, through sherpa-onnx) turns your words into text on the headset, in the main-menu language, in about a second and
a half. The audio never leaves the headset.

- **Download:** "Speech input · Whisper base" in the main menu (161 MB). **Try it** records a few words and shows what
  was heard.
- **Microphone:** the app asks for the permission the first time you talk.
- **What happens to the words:** in Chat and Garden they are sent as your message; in Point & Ask they fill in the
  question.
- **Limits:** a recording stops by itself after about 28 seconds (Whisper reads 30 s at most). Silence or a very short
  press returns nothing rather than invented text. Reading aloud stops while you talk, so the reply isn't recorded.

**Mic gesture.** Instead of pressing **Talk**, hold your hand to your mouth as if gripping a microphone:

- **Hand tracking:** make a closed fist and bring the top of it (thumb and index knuckle) to your mouth.
- **Controllers:** bring the controller to your mouth.

Hold it there for about a third of a second and listening starts; speak, then lower your hand or controller and what
you said is transcribed and used, exactly as with the Talk button. It drives the Talk button of the open panel (Chat,
Point & Ask or Tabletop Garden). With no such panel open, it only shows a hint. While you speak, the fist can loosen a
little without stopping.

**Start and stop signals.** However listening starts (Talk button, mic gesture, or **Try it** in the main menu):

- **Start:** a rising two-note tone and a red microphone symbol, low in your field of view, whose ring pulses with your
  voice and shows the seconds recorded.
- **Stop:** a falling tone and a grey crossed-out microphone, then "Transcribing…" until the text is ready.
- **Hints** (no panel to talk to, speech input not downloaded, nothing heard) show under a blue microphone with a soft
  tone.

The first quarter second of each recording is dropped, so the start tone isn't transcribed.

### My instructions

**My instructions** lets you tell the model, in plain words, how to reply. For example: "I'm on a strict diet. If I
point at cake or junk food, make fun of me for it." The instructions apply in Spatial Chat, Point & Ask and What's That?,
are stored on the headset, and can be turned off without deleting them. Open the editor from the launcher or from the
What's That? panel. It offers a few presets (Strict diet, Kid-friendly, Plant lover, Safety first, Pirate) as starting points.

## Requirements

- **Headset:** Meta Quest 3 or 3S. Passthrough Camera Access and depth need the Quest 3 family.
- **Unity:** 6000.6.7f1 with Android Build Support (OpenJDK, SDK and NDK).
- **Git LFS:** binaries (images, models, audio, video, native libraries) are stored in LFS. Install it before cloning:

  ```bash
  git lfs install
  ```

- **Storage on the headset:** about 3.3 GB for the models, both voices and speech input.
- **Hugging Face token:** only needed for Tiny Garden (see below).

## Getting started

1. Clone the repository with Git LFS installed, then open it in Unity 6000.6.7f1. The project is built on Unity's
   Mixed Reality template: XR Origin + XR Interaction Toolkit, AR Foundation, OpenXR with Meta features, and URP.
2. Switch the build target to **Android**. The only build scene is `Assets/Scenes/SampleScene.unity`.
3. Build and install, either from **File > Build And Run** or in batch mode (close the Editor first):

   ```bash
   "<Unity.exe>" -batchmode -quit -projectPath . -buildTarget Android -logFile -
   ```

4. On the headset, allow camera and spatial data access when asked. The **launcher** panel (main menu) opens in front
   of you. Pick **English** or **Italiano** at the top right, download the models, the voices and speech input, then
   open an experience. The microphone permission is asked the first time you press **Talk**.

### Models, voices and speech input

Downloads go to the app's storage (`/sdcard/Android/data/<package>/files/`) and are pinned to fixed Hugging Face
commits. They resume after interruption and can be deleted from the launcher.

| Item | Used by | Size | Access |
|---|---|---|---|
| Gemma 4 E2B-it (`litert-community/gemma-4-E2B-it-litert-lm`) | Chat, Point & Ask | 2.6 GB | Open |
| FunctionGemma 270M Tiny Garden (`litert-community/functiongemma-270m-ft-tiny-garden`) | Tabletop Garden | 289 MB | Gated (auto-approve) |
| Piper "alba" voice (`csukuangfj/vits-piper-en_GB-alba-medium`) | Spoken replies in English | 81 MB | Open |
| Piper "paola" voice (`csukuangfj/vits-piper-it_IT-paola-medium`) | Spoken replies in Italian | 82 MB | Open |
| Whisper base, int8 (`csukuangfj/sherpa-onnx-whisper-base`) | Speech input in English and Italian | 161 MB | Open |

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
  Core/                        LLM engine and service, model catalog/downloader/store, speech engine, speech input, voice pack,
                               app language, language detection
  Camera/                      Passthrough camera capture and crops
  Spatial/                     Depth service, depth-based measurement and placement
  Experiences/Chat|AskImage|TinyGarden|WhatsThat/
  Interaction/                 Mic gesture (fist or controller at the mouth) and its geometry
  UI/                          Launcher, UI factory, Markdown → TextMeshPro, voice toggle, Talk button, mic signals
  Diagnostics/SelfTest.cs      On-device self-tests
  Tests/Editor/                EditMode unit tests
Assets/Plugins/Android/LiteRtBridge.androidlib/
                               Kotlin bridge: LiteRT-LM (LlmBridge), system TTS (SpeechBridge),
                               neural TTS (NeuralSpeechBridge), speech input (RecognizerBridge),
                               sherpa-onnx jar + arm64 native library
Assets/MRTemplateAssets/       Unity MR template code and assets
```

Notes:

- **Bridge:** the Kotlin bridge is a Gradle library module that Unity builds with the app. LiteRT-LM
  (`com.google.ai.edge.litertlm:litertlm-android:0.18.1`) comes from Maven. Its manifest entries are merged by Gradle,
  so the SDK-generated `Assets/Plugins/Android/AndroidManifest.xml` is never edited by hand. The bridge's own manifest
  declares the camera and microphone (`RECORD_AUDIO`) permissions.
- **Rig:** the rig stays the template's XR Origin. Depth uses MRUK's `EnvironmentRaycastManager`, and image input uses
  Passthrough Camera Access.

## Testing

**EditMode unit tests** (text processing, chat history, garden rules, depth math, catalog, downloads, app language and
reply prompts, personal instructions, voice and speech-input packs, mic gesture, person boxes and matching):

```bash
"<Unity.exe>" -batchmode -projectPath . -runTests -testPlatform EditMode -testFilter AIEdge -testResults results.xml -logFile -
```

**On-device self-tests:**

1. Push a `selftest.json` to the app's files folder and launch the app. For example, `{"tests":["chat_multiturn","speech"]}` runs only the named tests; any other content runs all of them.
2. The available tests are:
   - Downloads: `resume_download`, `small_download`, `gated_without_token`, `cancel_and_reset`, `unload_memory`
   - Chat and Point & Ask: `chat_multiturn`, `ask_image`
   - What's That?: `whats_that`, `people` (needs `people_test.jpg` next to `selftest.json`, such as TensorFlow's
     [beach test photo](https://github.com/tensorflow/models/blob/master/research/object_detection/test_images/image2.jpg):
     the people at its bottom left must be found from the file and through the live GPU path, then the live camera must
     run at 2 frames a second or more)
   - My instructions: `instructions`, `instructions_it`
   - Garden: `garden`, `garden_view`
   - Depth: `depth`, `depth_ask`, `depth_place`, `depth_garden`
   - Voice: `speech`, `speech_it` (with Italiano and an English instruction on, the reply must be all Italian and
     read only by the Italian voice)
   - Speech input: `stt` (each voice says a known sentence, Whisper must transcribe it in that language; then a short
     real microphone recording), `mic_gesture` (a simulated fist at the mouth: only a hint with no panel open; in Chat
     it starts listening with the start signal, keeps listening while the fist loosens, and stops with the stop signal
     when moved away)
3. Results are logged as `SELFTEST|{json}` lines (logcat tag `Unity`) and written to `selftest_results.txt` in the same folder.
4. Delete `selftest.json` afterwards, or the tests run on every launch.

## Credits and licenses

Thanks to the [Google AI Edge](https://github.com/google-ai-edge) team. This port would not exist without their
open-source Gallery app and LiteRT-LM runtime.

This repository contains only original code and Unity template assets. The models, the voices and the speech
recognition model are downloaded at runtime under their own terms:

| Component | License / terms |
|---|---|
| [Google AI Edge Gallery](https://github.com/google-ai-edge/gallery): experience design, Tiny Garden system prompt and tool schema | Apache 2.0 |
| [LiteRT-LM](https://github.com/google-ai-edge/LiteRT-LM) runtime | Apache 2.0 |
| Gemma 4 E2B and FunctionGemma 270M Tiny Garden models | [Gemma Terms of Use](https://ai.google.dev/gemma/terms) |
| [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) v1.13.8 (bundled jar and `libsherpa-onnx-jni.so`, includes ONNX Runtime, MIT) | Apache 2.0 |
| [Piper](https://github.com/rhasspy/piper) "alba" voice: training data from the [University of Edinburgh](https://datashare.ed.ac.uk/handle/10283/3270) | CC BY 4.0 |
| [Piper](https://github.com/rhasspy/piper) "paola" voice: training data from [paolapersico1/Voice-Dataset-Italian](https://huggingface.co/datasets/paolapersico1/Voice-Dataset-Italian) | CC0 1.0 |
| espeak-ng data, downloaded with each voice for phonemization | GPL-3.0 |
| [Whisper](https://github.com/openai/whisper) base, OpenAI (ONNX export by sherpa-onnx), downloaded for speech input | MIT |
| [LiteRT](https://github.com/google-ai-edge/LiteRT) 2.3.0 interpreter (`com.google.ai.edge.litert:litert`) | Apache 2.0 |
| [EfficientDet-Lite0](https://www.kaggle.com/models/tensorflow/efficientdet/tfLite/lite0-detection-metadata) COCO detector, TensorFlow (`efficientdet_lite0.tflite`, bundled) | Apache 2.0 |
| MediaPipe [interactive segmenter](https://ai.google.dev/edge/mediapipe/solutions/vision/interactive_segmenter) "magic touch" model (`magic_touch.tflite`, bundled) | See its model card |
| Unity Mixed Reality template and package samples | Unity Companion License |

Tiny Garden's original game art and code are not used. The garden is rebuilt in Unity from primitives.
