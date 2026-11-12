package com.xraiedge.litertlm

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Log
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import org.json.JSONArray
import org.json.JSONObject
import org.tensorflow.lite.Interpreter

/**
 * Person detection: finds people in a camera frame and returns their bounding boxes. Runs EfficientDet-Lite0
 * (`efficientdet_lite0.tflite`, bundled in the app's assets; TensorFlow's COCO detector with its post-processing
 * built in) with the LiteRT interpreter on the CPU. Nothing leaves the device.
 *
 * The model takes a 320×320 RGB image in bytes and returns up to 25 boxes (ymin, xmin, ymax, xmax, normalized),
 * classes and scores; class 0 is "person". Only people are reported.
 *
 * One frame is processed at a time on a background thread: a frame offered while the previous one is still running
 * is dropped, so a slow device simply runs at a lower rate. Results arrive through [Listener] on that thread.
 */
class DetectorBridge(context: Context, private val listener: Listener) {

  /** Implemented in C# via AndroidJavaProxy. */
  interface Listener {
    /** [resultJson]: {"people":[{"x","y","w","h","score"}...],"inferMs":..} with boxes normalized, origin top-left. */
    fun onDetections(requestId: String, resultJson: String)

    fun onError(requestId: String, message: String)
  }

  private val appContext = context.applicationContext
  private val executor = Executors.newSingleThreadExecutor { r -> Thread(r, "AIEdgeDetector") }
  private val busy = AtomicBoolean(false)
  @Volatile private var interpreter: Interpreter? = null
  private val input = ByteBuffer.allocateDirect(SIZE * SIZE * 3).order(ByteOrder.nativeOrder())
  private var loadMs = 0L

  /** True while a frame is being processed. */
  fun isBusy(): Boolean = busy.get()

  /**
   * Detects people in a [SIZE]×[SIZE] RGBA frame ([rgba], 4 bytes per pixel). With [bottomUp] the first row is the
   * bottom of the image (as a Unity GPU readback delivers it). Returns false, dropping the frame, while busy.
   */
  fun detect(requestId: String, rgba: ByteArray, bottomUp: Boolean, minScore: Float): Boolean {
    if (rgba.size < SIZE * SIZE * 4 || !busy.compareAndSet(false, true)) return false
    executor.execute {
      try {
        synchronized(input) {
          input.rewind()
          for (row in 0 until SIZE) {
            val source = (if (bottomUp) SIZE - 1 - row else row) * SIZE * 4
            for (x in 0 until SIZE) {
              val i = source + x * 4
              input.put(rgba[i])
              input.put(rgba[i + 1])
              input.put(rgba[i + 2])
            }
          }
        }
        listener.onDetections(requestId, run(minScore).toString())
      } catch (e: Throwable) {
        Log.e(TAG, "Detection failed", e)
        listener.onError(requestId, e.message ?: e.toString())
      } finally {
        busy.set(false)
      }
    }
    return true
  }

  /** Detects people in an image file (any size; it is stretched to the model input), for tests. */
  fun detectFile(requestId: String, imagePath: String, minScore: Float) {
    executor.execute {
      try {
        val decoded = BitmapFactory.decodeFile(imagePath) ?: throw IllegalArgumentException("Can't read $imagePath")
        val bitmap = Bitmap.createScaledBitmap(decoded, SIZE, SIZE, true)
        if (bitmap !== decoded) decoded.recycle()
        val pixels = IntArray(SIZE * SIZE)
        bitmap.getPixels(pixels, 0, SIZE, 0, 0, SIZE, SIZE)
        bitmap.recycle()
        synchronized(input) {
          input.rewind()
          for (p in pixels) {
            input.put(((p shr 16) and 0xFF).toByte())
            input.put(((p shr 8) and 0xFF).toByte())
            input.put((p and 0xFF).toByte())
          }
        }
        listener.onDetections(requestId, run(minScore).toString())
      } catch (e: Throwable) {
        Log.e(TAG, "Detection failed", e)
        listener.onError(requestId, e.message ?: e.toString())
      }
    }
  }

  /** Releases the model. */
  fun close() {
    executor.execute {
      interpreter?.close()
      interpreter = null
    }
    executor.shutdown()
  }

  private fun load(): Interpreter {
    interpreter?.let { return it }
    val started = System.nanoTime()
    val bytes = appContext.assets.open(MODEL_ASSET).use { it.readBytes() }
    val model = ByteBuffer.allocateDirect(bytes.size).order(ByteOrder.nativeOrder())
    model.put(bytes).rewind()
    val created = Interpreter(model, Interpreter.Options().setNumThreads(THREADS))
    created.allocateTensors()
    loadMs = (System.nanoTime() - started) / 1_000_000
    interpreter = created
    return created
  }

  private fun run(minScore: Float): JSONObject {
    val model = load()
    // Outputs, in order: locations [1, N, 4], classes [1, N], scores [1, N], count [1].
    val count = model.getOutputTensor(0).shape().let { if (it.size >= 2) it[1] else MAX_BOXES }
    val boxes = Array(1) { Array(count) { FloatArray(4) } }
    val classes = Array(1) { FloatArray(count) }
    val scores = Array(1) { FloatArray(count) }
    val found = FloatArray(1)
    val outputs = HashMap<Int, Any>()
    outputs[0] = boxes
    outputs[1] = classes
    outputs[2] = scores
    outputs[3] = found

    val started = System.nanoTime()
    synchronized(input) {
      input.rewind()
      model.runForMultipleInputsOutputs(arrayOf<Any>(input), outputs)
    }
    val inferMs = (System.nanoTime() - started) / 1_000_000

    val people = JSONArray()
    val n = found[0].toInt().coerceIn(0, count)
    var bestPerson = 0f
    for (i in 0 until n) {
      if (classes[0][i].toInt() != PERSON) continue
      bestPerson = maxOf(bestPerson, scores[0][i])
      if (scores[0][i] < minScore) continue
      val (yMin, xMin, yMax, xMax) = boxes[0][i].map { it.coerceIn(0f, 1f) }
      if (xMax <= xMin || yMax <= yMin) continue
      people.put(
        JSONObject()
          .put("x", xMin.toDouble())
          .put("y", yMin.toDouble())
          .put("w", (xMax - xMin).toDouble())
          .put("h", (yMax - yMin).toDouble())
          .put("score", scores[0][i].toDouble())
      )
    }

    return JSONObject()
      .put("people", people)
      .put("detections", n)
      .put("bestPerson", bestPerson.toDouble())
      .put("inferMs", inferMs)
      .put("loadMs", loadMs)
  }

  companion object {
    private const val TAG = "AIEdgeDetector"
    const val SIZE = 320
    private const val MAX_BOXES = 25
    private const val PERSON = 0
    private const val MODEL_ASSET = "efficientdet_lite0.tflite"
    private const val THREADS = 2
  }
}
