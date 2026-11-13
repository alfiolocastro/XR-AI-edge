package com.xraiedge.litertlm

import android.app.Activity
import android.content.Context
import android.media.AudioManager
import android.util.Log
import android.view.KeyEvent
import android.view.Window

/**
 * The headset's physical volume-up key, for the app's own use: holding it switches person detection on or off.
 *
 * The activity window's callback is wrapped so volume-up presses reach [Listener] instead of the system. Presses are
 * reported as down and up (key repeats are dropped); the app decides what a hold means. Since the key no longer
 * changes the volume by itself, a short press should call [raiseVolume]. Volume down and every other key pass through
 * untouched.
 */
class KeyBridge(private val activity: Activity, private val listener: Listener) {

  /** Implemented in C# via AndroidJavaProxy. Called on the UI thread. */
  interface Listener {
    fun onVolumeUp(down: Boolean)
  }

  private val audio = activity.getSystemService(Context.AUDIO_SERVICE) as AudioManager
  @Volatile private var original: Window.Callback? = null

  init {
    activity.runOnUiThread {
      val window = activity.window
      val wrapped = window.callback
      original = wrapped
      window.callback = object : Window.Callback by wrapped {
        override fun dispatchKeyEvent(event: KeyEvent): Boolean {
          if (event.keyCode != KeyEvent.KEYCODE_VOLUME_UP) return wrapped.dispatchKeyEvent(event)
          when {
            event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0 -> listener.onVolumeUp(true)
            event.action == KeyEvent.ACTION_UP -> listener.onVolumeUp(false)
          }
          return true
        }
      }
      Log.i(TAG, "Listening to the volume-up key")
    }
  }

  /** Raises the media volume one step and shows the system volume indicator, as the key normally would. */
  fun raiseVolume() {
    audio.adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_RAISE, AudioManager.FLAG_SHOW_UI)
  }

  /** Gives the key back to the system. */
  fun close() {
    activity.runOnUiThread { original?.let { activity.window.callback = it } }
  }

  companion object {
    private const val TAG = "AIEdgeKeys"
  }
}
