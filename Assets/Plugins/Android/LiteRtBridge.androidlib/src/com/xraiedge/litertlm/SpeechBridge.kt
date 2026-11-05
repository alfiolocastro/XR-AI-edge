package com.xraiedge.litertlm

import android.content.Context
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import android.util.Log
import java.util.Locale
import org.json.JSONArray
import org.json.JSONObject

/**
 * On-device text to speech for Unity through Android [TextToSpeech]. On Quest the system engine is Meta's
 * `com.oculus.systemintelligence` on-device synthesis service, so speech works offline like the models.
 *
 * Created and driven from C# with AndroidJavaObject. Callbacks arrive on Android threads; the C# side moves them
 * to the Unity main thread.
 */
class SpeechBridge(context: Context, private val listener: Listener) {

  /** Implemented in C# via AndroidJavaProxy. */
  interface Listener {
    fun onReady(infoJson: String)
    fun onError(message: String)
    fun onStart(utteranceId: String)
    fun onDone(utteranceId: String)
  }

  private val appContext = context.applicationContext
  private val mainHandler = Handler(Looper.getMainLooper())
  @Volatile private var tts: TextToSpeech? = null
  @Volatile private var ready = false

  init {
    // TextToSpeech binds a service and reports on the main looper; create it there.
    mainHandler.post {
      tts = TextToSpeech(appContext) { status -> onInit(status) }
    }
  }

  private fun onInit(status: Int) {
    val engine = tts
    if (status != TextToSpeech.SUCCESS || engine == null) {
      listener.onError("TextToSpeech init failed ($status)")
      return
    }

    engine.setOnUtteranceProgressListener(
      object : UtteranceProgressListener() {
        override fun onStart(utteranceId: String) = listener.onStart(utteranceId)

        override fun onDone(utteranceId: String) = listener.onDone(utteranceId)

        override fun onStop(utteranceId: String, interrupted: Boolean) = listener.onDone(utteranceId)

        @Deprecated("Deprecated in Java")
        override fun onError(utteranceId: String) {
          listener.onDone(utteranceId)
        }

        override fun onError(utteranceId: String, errorCode: Int) {
          Log.w(TAG, "Utterance $utteranceId failed: $errorCode")
          listener.onDone(utteranceId)
        }
      }
    )
    ready = true

    val info = JSONObject()
    info.put("engine", engine.defaultEngine ?: "")
    info.put("engines", JSONArray(engine.engines.map { it.name }))
    info.put("maxInputLength", TextToSpeech.getMaxSpeechInputLength())
    info.put("voice", engine.voice?.name ?: "")
    info.put("language", engine.voice?.locale?.toLanguageTag() ?: "")
    listener.onReady(info.toString())
  }

  /** True once the engine is bound and can speak. */
  fun isReady(): Boolean = ready

  /**
   * Sets the speech language from a BCP 47 tag such as "en-US". Returns the TextToSpeech availability code
   * (0 or more is available; negative is missing or not supported).
   */
  fun setLanguage(languageTag: String): Int = tts?.setLanguage(Locale.forLanguageTag(languageTag)) ?: TextToSpeech.ERROR

  /** Speech rate: 1 is normal. */
  fun setRate(rate: Float) {
    tts?.setSpeechRate(rate)
  }

  /** Queues text (or replaces the queue when [flush]). Returns false if it can't be queued. */
  fun speak(text: String, utteranceId: String, flush: Boolean): Boolean {
    val engine = tts ?: return false
    if (!ready) return false
    val mode = if (flush) TextToSpeech.QUEUE_FLUSH else TextToSpeech.QUEUE_ADD
    return engine.speak(text, mode, Bundle(), utteranceId) == TextToSpeech.SUCCESS
  }

  /** Stops speaking and clears the queue. */
  fun stop() {
    tts?.stop()
  }

  /** True while audio is playing or queued. */
  fun isSpeaking(): Boolean = tts?.isSpeaking ?: false

  /** Releases the engine. */
  fun shutdown() {
    ready = false
    val engine = tts
    tts = null
    mainHandler.post { engine?.shutdown() }
  }

  companion object {
    private const val TAG = "AIEdgeSpeech"
  }
}
