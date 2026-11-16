using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AIEdge
{
    /// <summary>
    /// Watches frame times and logs stalls (frames far longer than the display's), so a freeze can be traced to what
    /// was running. Self-tests read the stats over a span with <see cref="Mark"/> and <see cref="Since"/>.
    /// </summary>
    public class FrameHitches : MonoBehaviour
    {
        /// <summary>Frames longer than this are logged as stalls.</summary>
        public const float LogSeconds = 0.1f;

        /// <summary>Frames longer than this count as dropped.</summary>
        public const float SlowSeconds = 0.025f;

        static int s_Frames;
        static int s_Slow;
        static float s_Worst;
        static float s_StallSeconds;

        /// <summary>Totals since the app started; subtract a <see cref="Mark"/> for a span.</summary>
        public struct Stats
        {
            public int frames;
            public int slow;
            public float worst;
            public float stallSeconds;

            public override string ToString() =>
                $"{frames} frames, {slow} slow, worst {worst * 1000f:0} ms, {stallSeconds:0.00} s in stalls";
        }

        /// <summary>Starts a span: remember this and pass it to <see cref="Since"/>.</summary>
        public static Stats Mark()
        {
            var mark = new Stats { frames = s_Frames, slow = s_Slow, stallSeconds = s_StallSeconds };
            s_Worst = 0f;
            return mark;
        }

        /// <summary>Frame stats since a <see cref="Mark"/> (worst frame since the last mark).</summary>
        public static Stats Since(Stats mark) => new Stats
        {
            frames = s_Frames - mark.frames,
            slow = s_Slow - mark.slow,
            worst = s_Worst,
            stallSeconds = s_StallSeconds - mark.stallSeconds,
        };

        /// <summary>Times a piece of main-thread work and logs it when it is long enough to drop frames.</summary>
        public static Stopwatch Time() => Stopwatch.StartNew();

        /// <summary>Logs the time taken since <paramref name="watch"/> started, if it could be felt.</summary>
        public static void Log(Stopwatch watch, string what)
        {
            var ms = watch.Elapsed.TotalMilliseconds;
            if (ms >= SlowSeconds * 1000f)
                Debug.Log($"[AIEdge] Main thread: {what} took {ms:0} ms");
        }

        void Update()
        {
            // The first frames after launch or a pause are long for unrelated reasons.
            if (UnityEngine.Time.frameCount < 10)
                return;
            var dt = UnityEngine.Time.unscaledDeltaTime;
            s_Frames++;
            if (dt > SlowSeconds)
                s_Slow++;
            if (dt > s_Worst)
                s_Worst = dt;
            if (dt < LogSeconds)
                return;
            s_StallSeconds += dt;
            var llm = LlmService.Instance;
            Debug.Log($"[AIEdge] Stall: frame took {dt * 1000f:0} ms (model {(llm != null && llm.Engine.IsBusy ? "busy" : "idle")})");
        }

        void OnApplicationPause(bool paused)
        {
            // Time spent paused isn't a stall.
            if (!paused)
                s_Worst = 0f;
        }
    }
}
