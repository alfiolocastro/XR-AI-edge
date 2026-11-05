package com.xraiedge.litertlm

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Log
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.Executors
import kotlin.math.exp
import org.json.JSONObject
import org.tensorflow.lite.Interpreter

/**
 * Interactive segmentation: cuts out the object at a pointed pixel. Runs Google's MediaPipe "magic touch" model
 * (`magic_touch.tflite`, bundled in the app's assets) directly with the LiteRT interpreter on the CPU, without the
 * MediaPipe Tasks library (which sends usage statistics to Google).
 *
 * The model takes a 512×512 RGBA image scaled to [0, 1] whose alpha channel marks the pointed spot (as MediaPipe's
 * interactive segmenter graph draws it: 1 at the keypoint, 0 elsewhere), and returns a 512×512 mask.
 *
 * Calls run on one background thread; results arrive through [Listener] on that thread.
 */
class SegmenterBridge(context: Context, private val listener: Listener) {

  /** Implemented in C# via AndroidJavaProxy. */
  interface Listener {
    /** The mask was written to the requested file (512×512 bytes, row 0 at the top); [infoJson] has stats. */
    fun onMask(requestId: String, infoJson: String)

    fun onError(requestId: String, message: String)
  }

  private val appContext = context.applicationContext
  private val executor = Executors.newSingleThreadExecutor { r -> Thread(r, "AIEdgeSegmenter") }
  @Volatile private var interpreter: Interpreter? = null
  private val input = ByteBuffer.allocateDirect(SIZE * SIZE * 4 * 4).order(ByteOrder.nativeOrder())
  private val output = ByteBuffer.allocateDirect(SIZE * SIZE * 4).order(ByteOrder.nativeOrder())

  /**
   * Segments the object at ([keyX], [keyY]) in the PNG at [imagePath] (normalized, origin at the top-left) and
   * writes the mask to [maskPath].
   */
  fun segment(requestId: String, imagePath: String, keyX: Float, keyY: Float, maskPath: String) {
    executor.execute {
      try {
        val info = run(imagePath, keyX, keyY, maskPath)
        listener.onMask(requestId, info.toString())
      } catch (e: Throwable) {
        Log.e(TAG, "Segmentation failed", e)
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
    // Read the whole model into a direct buffer: works whether or not the asset is stored compressed.
    val bytes = appContext.assets.open(MODEL_ASSET).use { it.readBytes() }
    val model = ByteBuffer.allocateDirect(bytes.size).order(ByteOrder.nativeOrder())
    model.put(bytes).rewind()
    val options = Interpreter.Options().setNumThreads(THREADS)
    val created = Interpreter(model, options)
    loadMs = (System.nanoTime() - started) / 1_000_000
    interpreter = created
    return created
  }

  private var loadMs = 0L

  private fun run(imagePath: String, keyX: Float, keyY: Float, maskPath: String): JSONObject {
    val model = load()
    val decoded = BitmapFactory.decodeFile(imagePath) ?: throw IllegalArgumentException("Can't read $imagePath")
    val bitmap = Bitmap.createScaledBitmap(decoded, SIZE, SIZE, true)
    if (bitmap !== decoded) decoded.recycle()

    // RGB in [0, 1]; alpha marks the pointed spot (a small disk at model resolution).
    val pixels = IntArray(SIZE * SIZE)
    bitmap.getPixels(pixels, 0, SIZE, 0, 0, SIZE, SIZE)
    bitmap.recycle()
    val kx = keyX.coerceIn(0f, 1f) * SIZE
    val ky = keyY.coerceIn(0f, 1f) * SIZE
    input.rewind()
    for (y in 0 until SIZE) {
      for (x in 0 until SIZE) {
        val p = pixels[y * SIZE + x]
        input.putFloat(((p shr 16) and 0xFF) / 255f)
        input.putFloat(((p shr 8) and 0xFF) / 255f)
        input.putFloat((p and 0xFF) / 255f)
        val dx = x + 0.5f - kx
        val dy = y + 0.5f - ky
        input.putFloat(if (dx * dx + dy * dy <= KEY_RADIUS * KEY_RADIUS) 1f else 0f)
      }
    }
    input.rewind()
    output.rewind()

    val started = System.nanoTime()
    model.run(input, output)
    val inferMs = (System.nanoTime() - started) / 1_000_000

    // Probabilities: the model may emit logits; squash them if the values leave [0, 1].
    output.rewind()
    val values = FloatArray(SIZE * SIZE)
    output.asFloatBuffer().get(values)
    var min = Float.MAX_VALUE
    var max = -Float.MAX_VALUE
    for (v in values) {
      if (v < min) min = v
      if (v > max) max = v
    }
    val logits = min < -0.01f || max > 1.01f
    val mask = ByteArray(SIZE * SIZE)
    var covered = 0
    for (i in values.indices) {
      val p = if (logits) 1f / (1f + exp(-values[i])) else values[i]
      mask[i] = (p.coerceIn(0f, 1f) * 255f).toInt().toByte()
      if (p > 0.5f) covered++
    }
    File(maskPath).writeBytes(mask)

    val keyIndex = ky.toInt().coerceIn(0, SIZE - 1) * SIZE + kx.toInt().coerceIn(0, SIZE - 1)
    return JSONObject()
      .put("width", SIZE)
      .put("height", SIZE)
      .put("coverage", covered.toDouble() / values.size)
      .put("atKey", (mask[keyIndex].toInt() and 0xFF) / 255.0)
      .put("logits", logits)
      .put("min", min.toDouble())
      .put("max", max.toDouble())
      .put("inferMs", inferMs)
      .put("loadMs", loadMs)
  }

  companion object {
    private const val TAG = "AIEdgeSegmenter"
    const val SIZE = 512
    private const val MODEL_ASSET = "magic_touch.tflite"
    private const val THREADS = 2
    private const val KEY_RADIUS = 1.5f
  }
}
