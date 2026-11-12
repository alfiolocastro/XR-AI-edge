using UnityEngine;

namespace AIEdge.Interaction
{
    /// <summary>
    /// Geometry of the "microphone" gesture: a closed fist, or a controller, held at the mouth as if it were a
    /// microphone. Plain C# so it can be unit tested.
    /// </summary>
    public static class MicGestureMath
    {
        /// <summary>A fingertip closer than this to the palm counts as curled, in meters.</summary>
        public const float CurledTipDistance = 0.065f;

        /// <summary>How far the mic head must come to the mouth to start listening, in meters.</summary>
        public const float StartDistance = 0.11f;

        /// <summary>How far the mic head must move away from the mouth to stop listening, in meters.</summary>
        public const float StopDistance = 0.2f;

        /// <summary>
        /// The mouth, from the head (camera) pose: the camera sits between the eyes, the mouth about 8 cm lower and
        /// 6 cm further forward.
        /// </summary>
        public static Vector3 Mouth(Vector3 headPosition, Quaternion headRotation) =>
            headPosition + headRotation * new Vector3(0f, -0.08f, 0.06f);

        /// <summary>
        /// True for a closed fist: at least three of the index, middle, ring and little fingertips curled against the
        /// palm.
        /// </summary>
        public static bool IsFist(Vector3 palm, Vector3 indexTip, Vector3 middleTip, Vector3 ringTip, Vector3 littleTip)
        {
            var curled = 0;
            foreach (var tip in new[] { indexTip, middleTip, ringTip, littleTip })
            {
                if (Vector3.Distance(tip, palm) < CurledTipDistance)
                    curled++;
            }

            return curled >= 3;
        }

        /// <summary>
        /// The "head" of a fist held like a microphone: the top of the fist, where the thumb meets the index knuckle.
        /// </summary>
        public static Vector3 FistHead(Vector3 indexKnuckle, Vector3 thumbTip) => Vector3.Lerp(indexKnuckle, thumbTip, 0.5f);
    }

    /// <summary>What a <see cref="MicGestureTracker"/> update decided.</summary>
    public enum MicGestureEvent
    {
        None,
        /// <summary>The mic was brought to the mouth and held there: start listening.</summary>
        Start,
        /// <summary>The mic moved away from the mouth (or tracking was lost): stop listening.</summary>
        Stop,
    }

    /// <summary>
    /// Turns the distance between the mic (fist or controller) and the mouth into start and stop events, with
    /// hysteresis and hold times so brushing past the face doesn't trigger it, and a brief wobble while talking
    /// doesn't stop it.
    /// </summary>
    public class MicGestureTracker
    {
        /// <summary>Seconds the mic must stay at the mouth before listening starts.</summary>
        public float StartHold = 0.35f;

        /// <summary>Seconds the mic must stay away (or untracked) before listening stops.</summary>
        public float StopHold = 0.3f;

        /// <summary>Seconds after a stop before a new start is allowed.</summary>
        public float Cooldown = 0.6f;

        /// <summary>True between a <see cref="MicGestureEvent.Start"/> and the next <see cref="MicGestureEvent.Stop"/>.</summary>
        public bool IsActive { get; private set; }

        float m_Near;
        float m_Away;
        float m_SinceStop = float.MaxValue;

        /// <summary>Clears the state without raising an event (for example when the gesture is switched off).</summary>
        public void Reset()
        {
            IsActive = false;
            m_Near = m_Away = 0f;
            m_SinceStop = float.MaxValue;
        }

        /// <summary>Advances by <paramref name="deltaTime"/> seconds.</summary>
        /// <param name="qualified">A mic is present: a tracked closed fist, or a tracked controller.</param>
        /// <param name="distance">Distance from the mic head to the mouth, in meters (ignored when not qualified).</param>
        public MicGestureEvent Update(bool qualified, float distance, float deltaTime)
        {
            m_SinceStop += deltaTime;
            if (!IsActive)
            {
                if (qualified && distance < MicGestureMath.StartDistance && m_SinceStop >= Cooldown)
                {
                    m_Near += deltaTime;
                    if (m_Near >= StartHold)
                    {
                        IsActive = true;
                        m_Near = m_Away = 0f;
                        return MicGestureEvent.Start;
                    }
                }
                else
                {
                    m_Near = 0f;
                }

                return MicGestureEvent.None;
            }

            // While listening, a fist that loosens a little still counts; only moving away (or losing the hand) stops.
            var away = !qualified && distance >= MicGestureMath.StartDistance || distance > MicGestureMath.StopDistance;
            m_Away = away ? m_Away + deltaTime : 0f;
            if (m_Away < StopHold)
                return MicGestureEvent.None;

            IsActive = false;
            m_Near = m_Away = 0f;
            m_SinceStop = 0f;
            return MicGestureEvent.Stop;
        }
    }
}
