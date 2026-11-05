package com.xraiedge.litertlm

import android.os.Debug
import android.util.Log
import com.google.ai.edge.litertlm.Backend
import com.google.ai.edge.litertlm.Content
import com.google.ai.edge.litertlm.Contents
import com.google.ai.edge.litertlm.Conversation
import com.google.ai.edge.litertlm.ConversationConfig
import com.google.ai.edge.litertlm.Engine
import com.google.ai.edge.litertlm.EngineConfig
import com.google.ai.edge.litertlm.ExperimentalApi
import com.google.ai.edge.litertlm.ExperimentalFlags
import com.google.ai.edge.litertlm.Message
import com.google.ai.edge.litertlm.MessageCallback
import com.google.ai.edge.litertlm.OpenApiTool
import com.google.ai.edge.litertlm.SamplerConfig
import com.google.ai.edge.litertlm.tool
import java.io.File
import java.util.concurrent.CancellationException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import org.json.JSONArray
import org.json.JSONObject

/**
 * Thin facade over LiteRT-LM for Unity, driven from C# with AndroidJavaObject.
 *
 * Written in Kotlin because LiteRT-LM ships Java 21 bytecode, which the JDK 17 javac bundled with
 * Unity can't read. All engine work runs on one background thread; results come back through
 * [Listener], which Unity implements with AndroidJavaProxy (callbacks arrive on that thread).
 */
class LlmBridge(private val listener: Listener) {

  /** Implemented in C# via AndroidJavaProxy. */
  interface Listener {
    fun onLoaded(infoJson: String)
    fun onToken(text: String)
    fun onDone(statsJson: String)
    fun onError(message: String)
    /** Called on the inference thread; the returned string is the tool result JSON. */
    fun onToolCall(name: String, argsJson: String): String?
  }

  private val executor =
    Executors.newSingleThreadExecutor { r ->
      Thread(r, "LiteRtLm").apply { priority = Thread.NORM_PRIORITY - 1 }
    }

  @Volatile private var engine: Engine? = null
  @Volatile private var conversation: Conversation? = null

  // Conversation setup and text history, kept so a stopped conversation can be rebuilt: LiteRT-LM's
  // cancel is sticky, so after a stop the next turn on the same conversation is cancelled at once.
  // Google AI Edge Gallery does the same (LlmChatViewModel: sessionStoppedByModel + initialMessages).
  // Touched only on the executor thread.
  private var conversationConfig: ConversationConfig? = null
  private var constrainedDecoding = false
  private val history = mutableListOf<Message>()
  private var rebuildBeforeNextTurn = false

  /**
   * Loads a .litertlm model, replacing any loaded model.
   *
   * @param backend "cpu" or "gpu"
   * @param visionBackend "none", "cpu" or "gpu" (needed for image input)
   * @param maxTokens KV-cache size, or <= 0 for the model default
   */
  fun load(modelPath: String, backend: String, visionBackend: String, maxTokens: Int, cacheDir: String) {
    executor.execute {
      try {
        closeInternal()
        require(File(modelPath).isFile) { "Model not found: $modelPath" }
        val start = System.nanoTime()
        val config =
          EngineConfig(
            modelPath = modelPath,
            backend = toBackend(backend),
            visionBackend = if (visionBackend == "none") null else toBackend(visionBackend),
            maxNumTokens = if (maxTokens > 0) maxTokens else null,
            cacheDir = cacheDir.ifEmpty { null },
          )
        engine = Engine(config).also { it.initialize() }
        val info =
          JSONObject()
            .put("backend", backend)
            .put("visionBackend", visionBackend)
            .put("initSec", (System.nanoTime() - start) / 1e9)
            .put("pssMb", pssMb())
        Log.i(TAG, "Loaded $modelPath $info")
        listener.onLoaded(info.toString())
      } catch (t: Throwable) {
        fail("load", t)
      }
    }
  }

  /**
   * Starts a fresh conversation (drops the KV cache).
   *
   * @param toolsJson JSON array of OpenAPI tool descriptions, or empty for none
   * @param constrainedDecoding force tool calls to follow the tool schema (Tiny Garden uses this)
   * @param historyJson JSON array of earlier turns `[{"role":"user"|"model","text":"..."}]` to resume a
   *   chat, or empty to start fresh
   */
  fun newConversation(
    systemPrompt: String,
    topK: Int,
    topP: Double,
    temperature: Double,
    toolsJson: String,
    constrainedDecoding: Boolean,
    historyJson: String,
  ) {
    executor.execute {
      try {
        checkNotNull(engine) { "Model not loaded" }
        val tools =
          if (toolsJson.isEmpty()) emptyList()
          else {
            val array = JSONArray(toolsJson)
            (0 until array.length()).map { tool(ForwardingTool(array.getJSONObject(it))) }
          }
        conversationConfig =
          ConversationConfig(
            systemInstruction = if (systemPrompt.isEmpty()) null else Contents.of(systemPrompt),
            tools = tools,
            samplerConfig = SamplerConfig(topK = topK, topP = topP, temperature = temperature),
          )
        this.constrainedDecoding = constrainedDecoding
        history.clear()
        history.addAll(parseHistory(historyJson))
        rebuildBeforeNextTurn = false
        createConversation(history.toList())
      } catch (t: Throwable) {
        fail("newConversation", t)
      }
    }
  }

  private fun parseHistory(historyJson: String): List<Message> {
    if (historyJson.isEmpty()) return emptyList()
    val array = JSONArray(historyJson)
    return (0 until array.length()).map {
      val turn = array.getJSONObject(it)
      val text = turn.getString("text")
      if (turn.getString("role") == "model") Message.model(text) else Message.user(text)
    }
  }

  @OptIn(ExperimentalApi::class)
  private fun createConversation(initialMessages: List<Message>) {
    val e = checkNotNull(engine) { "Model not loaded" }
    val config = checkNotNull(conversationConfig) { "No conversation" }
    conversation?.close()
    // Global flag read when the conversation is created, as Google AI Edge Gallery does.
    ExperimentalFlags.enableConversationConstrainedDecoding = constrainedDecoding
    conversation = e.createConversation(config.copy(initialMessages = initialMessages))
  }

  /**
   * Sends a user turn. Streams via onToken, then onDone.
   *
   * @param imagePath PNG/JPEG file to attach, or empty for text only. A path avoids marshalling large
   *   byte arrays through JNI from C#.
   */
  fun send(text: String, imagePath: String) {
    executor.execute {
      try {
        if (rebuildBeforeNextTurn) {
          createConversation(history.toList())
          rebuildBeforeNextTurn = false
          Log.i(TAG, "Rebuilt conversation after stop with ${history.size} messages")
        }
        val conv = checkNotNull(conversation) { "No conversation" }
        val reply = StringBuilder()
        val contents = buildList {
          if (imagePath.isNotEmpty()) add(Content.ImageBytes(File(imagePath).readBytes()))
          add(Content.Text(text))
        }

        val tokensBefore = conv.getTokenCount()
        val start = System.nanoTime()
        var firstTokenAt = 0L
        var chunks = 0
        val finished = CountDownLatch(1)

        conv.sendMessageAsync(
          Message.of(contents),
          object : MessageCallback {
            override fun onMessage(message: Message) {
              val piece = message.toString()
              if (piece.isEmpty()) return
              if (firstTokenAt == 0L) firstTokenAt = System.nanoTime()
              chunks++
              reply.append(piece)
              listener.onToken(piece)
            }

            override fun onDone() = finish(cancelled = false)

            override fun onError(throwable: Throwable) {
              // A user stop arrives as CancellationException: report it as a finished turn.
              if (throwable is CancellationException) {
                finish(cancelled = true)
              } else {
                fail("send", throwable)
                finished.countDown()
              }
            }

            private fun finish(cancelled: Boolean) {
              // Text-only history for rebuilding after a stop (images aren't replayed).
              history.add(Message.user(text))
              history.add(Message.model(reply.toString()))
              if (cancelled) rebuildBeforeNextTurn = true
              try {
                val end = System.nanoTime()
                val tokensAfter = runCatching { conv.getTokenCount() }.getOrDefault(tokensBefore)
                val decodeSec = if (firstTokenAt == 0L) 0.0 else (end - firstTokenAt) / 1e9
                val stats =
                  JSONObject()
                    .put("ttftSec", if (firstTokenAt == 0L) 0.0 else (firstTokenAt - start) / 1e9)
                    .put("decodeChunks", chunks)
                    .put("decodeTokPerSec", if (decodeSec > 0) (chunks - 1) / decodeSec else 0.0)
                    .put("totalSec", (end - start) / 1e9)
                    .put("kvTokens", tokensAfter)
                    .put("turnTokens", tokensAfter - tokensBefore)
                    .put("pssMb", pssMb())
                    .put("cancelled", cancelled)
                Log.i(TAG, "Done $stats")
                listener.onDone(stats.toString())
              } catch (t: Throwable) {
                fail("stats", t)
              } finally {
                finished.countDown()
              }
            }
          },
        )

        // Keep the executor busy until this turn ends so queued work (new turns, close) waits for it.
        finished.await()
      } catch (t: Throwable) {
        fail("send", t)
      }
    }
  }

  /** Stops the current generation. Safe to call from any thread. */
  fun cancel() {
    try {
      conversation?.cancelProcess()
    } catch (t: Throwable) {
      Log.w(TAG, "cancel failed", t)
    }
  }

  /** Releases the model and its memory; the bridge can load another model afterwards. */
  fun unload() {
    cancel()
    executor.execute {
      closeInternal()
      Log.i(TAG, "Unloaded, pss ${pssMb()} MB")
    }
  }

  fun close() {
    cancel()
    executor.execute { closeInternal() }
    executor.shutdown()
  }

  private fun closeInternal() {
    runCatching { conversation?.close() }
    conversation = null
    conversationConfig = null
    history.clear()
    rebuildBeforeNextTurn = false
    runCatching { engine?.close() }
    engine = null
  }

  private fun fail(stage: String, t: Throwable) {
    Log.e(TAG, "$stage failed", t)
    listener.onError("$stage: $t")
  }

  /** OpenAPI tool whose execution is forwarded to Unity. */
  private inner class ForwardingTool(private val description: JSONObject) : OpenApiTool {
    override fun getToolDescriptionJsonString(): String = description.toString()

    override fun execute(paramsJsonString: String): String =
      listener.onToolCall(description.optString("name"), paramsJsonString)?.ifEmpty { null }
        ?: """{"result":"success"}"""
  }

  companion object {
    private const val TAG = "AIEdgeLlm"

    /** Which OpenCL driver files exist and whether one loads (prerequisite for the GPU backend). */
    @JvmStatic
    fun probeGpu(): String {
      val found =
        listOf("/vendor/lib64/libOpenCL.so", "/system/vendor/lib64/libOpenCL.so", "/system/lib64/libOpenCL.so")
          .filter { File(it).exists() }
      val load =
        try {
          System.loadLibrary("OpenCL")
          "loaded"
        } catch (t: Throwable) {
          "failed: ${t.message}"
        }
      return "files=${found.joinToString(";")} load=$load"
    }

    private fun toBackend(name: String): Backend =
      if (name.equals("gpu", ignoreCase = true)) Backend.GPU() else Backend.CPU()

    private fun pssMb(): Long {
      val info = Debug.MemoryInfo()
      Debug.getMemoryInfo(info)
      return info.totalPss / 1024L
    }
  }
}
