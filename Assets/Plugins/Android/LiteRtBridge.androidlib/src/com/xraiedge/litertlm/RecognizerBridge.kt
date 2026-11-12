package com.xraiedge.litertlm

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.util.Log
import com.k2fsa.sherpa.onnx.FeatureConfig
import com.k2fsa.sherpa.onnx.OfflineModelConfig
import com.k2fsa.sherpa.onnx.OfflineRecognizer
import com.k2fsa.sherpa.onnx.OfflineRecognizerConfig
import com.k2fsa.sherpa.onnx.OfflineWhisperModelConfig
import java.io.File
import java.util.concurrent.LinkedBlockingQueue
import kotlin.math.sqrt
import org.json.JSONObject

/**
 * Offline speech to text: the microphone is recorded with [AudioRecord] and transcribed on the CPU by a Whisper
 * model run with sherpa-onnx. Nothing leaves the device. The language ("en", "it") is chosen per transcription.
 *
 * Push to talk: [startRecording], then [stopAndTranscribe] (or [cancel]). Loading and decoding run on one worker
 * thread; results come back through [Listener] on that thread.
 *
 * @param modelDir folder holding `*-encoder*.onnx`, `*-decoder*.onnx` and `*-tokens.txt`.
 * @param numThreads CPU threads for decoding.
 */
class RecognizerBridge(
  private val modelDir: String,
  private val numThreads: Int,
  private val listener: Listener,
) {
  /** Callbacks, on the worker thread. */
  interface Listener {
    fun onReady(infoJson: String)

    /** [text] is empty when nothing was said. [infoJson] holds timings and the audio length. */
    fun onResult(id: String, text: String, infoJson: String)

    fun onError(id: String, message: String)
  }

  private class Job(val id: String, val samples: FloatArray, val sampleRate: Int, val language: String, val peak: Float)

  private val jobs = LinkedBlockingQueue<Job>()
  private val worker = Thread({ work() }, "AIEdgeAsr")
  private val lock = Object()

  @Volatile private var recognizer: OfflineRecognizer? = null
  @Volatile private var ready = false
  @Volatile private var running = true
  @Volatile private var recording = false
  @Volatile private var level = 0f
  private var recordThread: Thread? = null
  private var recorded = ShortArray(0)
  private var recordedCount = 0
  private var currentLanguage = ""

  init {
    worker.start()
  }

  /** True once the model is loaded. */
  fun isReady(): Boolean = ready

  /** True while the microphone is being recorded. */
  fun isRecording(): Boolean = recording

  /** Microphone loudness of the last ~0.1 s, 0 to 1, for a level meter. */
  fun getLevel(): Float = level

  /** Seconds recorded so far. */
  fun getRecordedSeconds(): Float = synchronized(lock) { recordedCount / SAMPLE_RATE.toFloat() }

  /**
   * Starts recording the microphone (needs the RECORD_AUDIO permission). Returns false when the microphone can't
   * be opened. Recording stops by itself after [MAX_SECONDS].
   */
  @SuppressLint("MissingPermission")
  fun startRecording(): Boolean {
    if (recording) return true
    val minBuffer = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
    val record = try {
      AudioRecord(
        MediaRecorder.AudioSource.VOICE_RECOGNITION,
        SAMPLE_RATE,
        AudioFormat.CHANNEL_IN_MONO,
        AudioFormat.ENCODING_PCM_16BIT,
        maxOf(minBuffer, SAMPLE_RATE / 5 * 2),
      )
    } catch (e: Exception) {
      Log.e(TAG, "Microphone unavailable", e)
      return false
    }
    if (record.state != AudioRecord.STATE_INITIALIZED) {
      record.release()
      return false
    }

    synchronized(lock) {
      recorded = ShortArray(SAMPLE_RATE * 4)
      recordedCount = 0
    }
    level = 0f
    recording = true
    record.startRecording()
    recordThread = Thread({ recordLoop(record) }, "AIEdgeMic").also { it.start() }
    return true
  }

  /** Stops recording and transcribes what was recorded in [language]. The result arrives as [Listener.onResult]. */
  fun stopAndTranscribe(id: String, language: String) {
    stopRecording()
    val samples: FloatArray
    var peak = 0f
    synchronized(lock) {
      samples = FloatArray(recordedCount) { i ->
        val value = recorded[i] / 32768f
        peak = maxOf(peak, kotlin.math.abs(value))
        value
      }
    }
    jobs.put(Job(id, samples, SAMPLE_RATE, language, peak))
  }

  /** Stops recording and drops it. */
  fun cancel() {
    stopRecording()
    synchronized(lock) { recordedCount = 0 }
  }

  /** Transcribes audio given directly (mono, -1 to 1), for tests. The result arrives as [Listener.onResult]. */
  fun transcribe(id: String, samples: FloatArray, sampleRate: Int, language: String) {
    jobs.put(Job(id, samples, sampleRate, language, samples.maxOfOrNull { kotlin.math.abs(it) } ?: 0f))
  }

  /** Stops recording and releases the model. */
  fun shutdown() {
    ready = false
    running = false
    stopRecording()
    worker.interrupt()
  }

  private fun stopRecording() {
    recording = false
    recordThread?.join(500)
    recordThread = null
    level = 0f
  }

  private fun recordLoop(record: AudioRecord) {
    val chunk = ShortArray(SAMPLE_RATE / 10)
    try {
      while (recording) {
        val read = record.read(chunk, 0, chunk.size)
        if (read <= 0) continue
        var sum = 0.0
        for (i in 0 until read) sum += chunk[i] * chunk[i].toDouble()
        level = minOf(1f, (sqrt(sum / read) / 32768.0 * 8.0).toFloat())
        synchronized(lock) {
          if (recordedCount + read > recorded.size) {
            if (recorded.size >= SAMPLE_RATE * MAX_SECONDS) {
              recording = false
              return@synchronized
            }
            recorded = recorded.copyOf(minOf(recorded.size * 2, SAMPLE_RATE * MAX_SECONDS))
          }
          val count = minOf(read, recorded.size - recordedCount)
          System.arraycopy(chunk, 0, recorded, recordedCount, count)
          recordedCount += count
        }
      }
    } catch (e: Exception) {
      Log.e(TAG, "Recording failed", e)
    } finally {
      try {
        record.stop()
      } catch (_: IllegalStateException) {
      }
      record.release()
      recording = false
    }
  }

  private fun work() {
    val engine = try {
      load()
    } catch (e: Throwable) {
      Log.e(TAG, "Speech recognizer load failed", e)
      listener.onError("", "Speech recognizer failed to load: ${e.message}")
      return
    }

    try {
      while (running) {
        val job = jobs.take()
        try {
          listener.onResult(job.id, decode(engine, job), lastInfo)
        } catch (e: Throwable) {
          Log.e(TAG, "Transcription failed", e)
          listener.onError(job.id, "Transcription failed: ${e.message}")
        }
      }
    } catch (_: InterruptedException) {
    } finally {
      recognizer = null
      engine.release()
    }
  }

  private var lastInfo = "{}"

  private fun decode(engine: OfflineRecognizer, job: Job): String {
    val seconds = job.samples.size / job.sampleRate.toFloat()
    val info = JSONObject()
    info.put("audioSec", seconds.toDouble())
    info.put("peak", job.peak.toDouble())
    info.put("language", job.language)
    // Too short or silent: Whisper invents text from silence, so don't run it.
    if (seconds < MIN_SECONDS || job.peak < MIN_PEAK) {
      info.put("skipped", true)
      lastInfo = info.toString()
      return ""
    }

    if (job.language != currentLanguage) {
      engine.setConfig(configFor(job.language))
      currentLanguage = job.language
    }
    val started = System.nanoTime()
    val stream = engine.createStream()
    try {
      stream.acceptWaveform(job.samples, job.sampleRate)
      engine.decode(stream)
      val text = engine.getResult(stream).text.trim()
      info.put("decodeMs", (System.nanoTime() - started) / 1_000_000)
      lastInfo = info.toString()
      return text
    } finally {
      stream.release()
    }
  }

  private fun configFor(language: String): OfflineRecognizerConfig {
    val dir = File(modelDir)
    fun find(part: String): File =
      dir.listFiles { f -> f.name.contains(part) && f.name.endsWith(".onnx") }?.minByOrNull { it.length() }
        ?: throw IllegalStateException("No $part model in $modelDir")
    val tokens = dir.listFiles { f -> f.name.endsWith("tokens.txt") }?.firstOrNull()
      ?: throw IllegalStateException("No tokens file in $modelDir")
    return OfflineRecognizerConfig(
      featConfig = FeatureConfig(sampleRate = SAMPLE_RATE, featureDim = 80),
      modelConfig = OfflineModelConfig(
        whisper = OfflineWhisperModelConfig(
          encoder = find("encoder").path,
          decoder = find("decoder").path,
          language = language,
          task = "transcribe",
        ),
        tokens = tokens.path,
        numThreads = numThreads,
        debug = false,
        provider = "cpu",
        modelType = "whisper",
      ),
    )
  }

  private fun load(): OfflineRecognizer {
    val started = System.nanoTime()
    currentLanguage = "en"
    val engine = OfflineRecognizer(null, configFor(currentLanguage))
    val info = JSONObject()
    info.put("engine", "sherpa-onnx whisper")
    info.put("model", File(modelDir).name)
    info.put("numThreads", numThreads)
    info.put("loadMs", (System.nanoTime() - started) / 1_000_000)
    recognizer = engine
    ready = true
    listener.onReady(info.toString())
    return engine
  }

  companion object {
    private const val TAG = "AIEdgeAsr"
    private const val SAMPLE_RATE = 16000
    // Whisper reads at most 30 s at a time.
    private const val MAX_SECONDS = 28
    private const val MIN_SECONDS = 0.4f
    private const val MIN_PEAK = 0.01f
  }
}
