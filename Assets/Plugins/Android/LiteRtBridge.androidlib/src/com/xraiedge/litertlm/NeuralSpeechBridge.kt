package com.xraiedge.litertlm

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.util.Log
import com.k2fsa.sherpa.onnx.OfflineTts
import com.k2fsa.sherpa.onnx.OfflineTtsConfig
import com.k2fsa.sherpa.onnx.OfflineTtsModelConfig
import com.k2fsa.sherpa.onnx.OfflineTtsVitsModelConfig
import java.io.File
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicInteger
import org.json.JSONObject

/**
 * Offline neural text to speech: a Piper (VITS) voice run on the CPU by sherpa-onnx, played through an
 * [AudioTrack]. Used where the system TextToSpeech engine is missing or disabled (as on Quest).
 *
 * Same calls and [SpeechBridge.Listener] callbacks as [SpeechBridge], so the C# side drives either one.
 * Sentences are synthesized on one thread while the previous one plays on another, so speech keeps up with a
 * streamed reply after the first sentence.
 *
 * @param voiceDir folder holding the voice's `.onnx` model, `tokens.txt` and `espeak-ng-data/`.
 * @param numThreads CPU threads for synthesis. Kept low so rendering and the LLM keep their cores.
 */
class NeuralSpeechBridge(
  private val voiceDir: String,
  private val numThreads: Int,
  private val listener: SpeechBridge.Listener,
) {
  private class Utterance(val text: String, val id: String, val generation: Int)

  private class Clip(val samples: FloatArray, val id: String, val generation: Int)

  private val textQueue = LinkedBlockingQueue<Utterance>()
  private val audioQueue = LinkedBlockingQueue<Clip>()
  // Bumped by stop() so work queued or in flight before it is dropped.
  private val generation = AtomicInteger()
  private val pending = AtomicInteger()
  private val engineLock = Object()

  @Volatile private var tts: OfflineTts? = null
  @Volatile private var track: AudioTrack? = null
  @Volatile private var ready = false
  @Volatile private var running = true
  @Volatile private var rate = 1f

  private val synthThread = Thread({ synthLoop() }, "AIEdgeTtsSynth")
  private val playThread = Thread({ playLoop() }, "AIEdgeTtsPlay")

  init {
    synthThread.start()
  }

  /** True once the voice is loaded and can speak. */
  fun isReady(): Boolean = ready

  /** The voice speaks one language; kept for parity with [SpeechBridge]. Always 0 (available). */
  @Suppress("UNUSED_PARAMETER")
  fun setLanguage(languageTag: String): Int = 0

  /** Speech rate: 1 is normal. */
  fun setRate(rate: Float) {
    this.rate = rate.coerceIn(0.5f, 2f)
  }

  /** Queues text (or replaces the queue when [flush]). Returns false if it can't be queued. */
  fun speak(text: String, utteranceId: String, flush: Boolean): Boolean {
    if (!ready || text.isBlank()) return false
    if (flush) stop()
    pending.incrementAndGet()
    textQueue.put(Utterance(text, utteranceId, generation.get()))
    return true
  }

  /** Stops speaking and clears the queue. */
  fun stop() {
    generation.incrementAndGet()
    textQueue.clear()
    audioQueue.clear()
    pending.set(0)
    track?.let {
      try {
        it.pause()
        it.flush()
        it.play()
      } catch (e: IllegalStateException) {
        Log.w(TAG, "AudioTrack reset failed", e)
      }
    }
  }

  /** True while audio is being synthesized, queued or played. */
  fun isSpeaking(): Boolean = pending.get() > 0

  /**
   * Synthesizes [text] and returns the samples (mono, -1 to 1, at [getSampleRate]) without playing them, for
   * tests. Empty when the voice isn't loaded.
   */
  fun synthesize(text: String): FloatArray {
    val engine = tts ?: return FloatArray(0)
    return synchronized(engineLock) { engine.generate(text, 0, 1f).samples }
  }

  /** Sample rate of the voice, or 0 before it is loaded. */
  fun getSampleRate(): Int = tts?.sampleRate() ?: 0

  /** Stops speaking and releases the voice and audio output. */
  fun shutdown() {
    ready = false
    running = false
    stop()
    synthThread.interrupt()
    playThread.interrupt()
  }

  private fun synthLoop() {
    val engine = try {
      load()
    } catch (e: Throwable) {
      Log.e(TAG, "Voice load failed", e)
      listener.onError("Neural voice failed to load: ${e.message}")
      return
    }

    try {
      while (running) {
        val utterance = textQueue.take()
        if (utterance.generation != generation.get()) continue
        val audio = synchronized(engineLock) { engine.generate(utterance.text, 0, rate) }
        if (utterance.generation != generation.get()) continue
        audioQueue.put(Clip(audio.samples, utterance.id, utterance.generation))
      }
    } catch (_: InterruptedException) {
    } catch (e: Throwable) {
      Log.e(TAG, "Synthesis failed", e)
      listener.onError("Synthesis failed: ${e.message}")
    } finally {
      tts = null
      engine.release()
    }
  }

  private fun load(): OfflineTts {
    val dir = File(voiceDir)
    val model = dir.listFiles { f -> f.name.endsWith(".onnx") }?.firstOrNull()
      ?: throw IllegalStateException("No .onnx voice in $voiceDir")
    val started = System.nanoTime()
    val config = OfflineTtsConfig(
      model = OfflineTtsModelConfig(
        vits = OfflineTtsVitsModelConfig(
          model = model.path,
          tokens = File(dir, "tokens.txt").path,
          dataDir = File(dir, "espeak-ng-data").path,
        ),
        numThreads = numThreads,
        debug = false,
        provider = "cpu",
      ),
      maxNumSentences = 1,
    )
    // A null AssetManager makes sherpa-onnx read plain file paths.
    val engine = OfflineTts(null, config)
    val loadMs = (System.nanoTime() - started) / 1_000_000

    val sampleRate = engine.sampleRate()
    track = AudioTrack.Builder()
      .setAudioAttributes(
        AudioAttributes.Builder()
          .setUsage(AudioAttributes.USAGE_ASSISTANT)
          .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH)
          .build()
      )
      .setAudioFormat(
        AudioFormat.Builder()
          .setEncoding(AudioFormat.ENCODING_PCM_FLOAT)
          .setSampleRate(sampleRate)
          .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
          .build()
      )
      .setBufferSizeInBytes(
        AudioTrack.getMinBufferSize(sampleRate, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_FLOAT) * 2
      )
      .setTransferMode(AudioTrack.MODE_STREAM)
      .build()
      .also { it.play() }
    playThread.start()

    tts = engine
    ready = true
    val info = JSONObject()
    info.put("engine", "sherpa-onnx")
    info.put("voice", model.nameWithoutExtension)
    info.put("sampleRate", sampleRate)
    info.put("numThreads", numThreads)
    info.put("loadMs", loadMs)
    listener.onReady(info.toString())
    return engine
  }

  private fun playLoop() {
    try {
      while (running) {
        val clip = audioQueue.take()
        val out = track ?: break
        if (clip.generation != generation.get()) continue
        listener.onStart(clip.id)
        var offset = 0
        while (offset < clip.samples.size && clip.generation == generation.get()) {
          val count = minOf(CHUNK, clip.samples.size - offset)
          val written = out.write(clip.samples, offset, count, AudioTrack.WRITE_BLOCKING)
          if (written <= 0) break
          offset += written
        }
        if (clip.generation == generation.get()) pending.updateAndGet { maxOf(0, it - 1) }
        listener.onDone(clip.id)
      }
    } catch (_: InterruptedException) {
    } finally {
      track?.release()
      track = null
    }
  }

  companion object {
    private const val TAG = "AIEdgeSpeech"
    // Small writes so stop() takes effect within ~0.1 s.
    private const val CHUNK = 2048
  }
}
