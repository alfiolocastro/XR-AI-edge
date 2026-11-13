namespace AIEdge.WhatsThat
{
    /// <summary>What a key did, as seen by <see cref="KeyHoldTracker"/>.</summary>
    public enum KeyHoldEvent
    {
        None,

        /// <summary>Released before the hold time: an ordinary press.</summary>
        Press,

        /// <summary>Held for the hold time (raised once, while still held).</summary>
        Hold,
    }

    /// <summary>
    /// Tells a press from a hold of a key: a hold is raised as soon as the key has been down for
    /// <see cref="HoldSeconds"/>, without waiting for its release; a release before that is a press. Plain C# so it can
    /// be unit tested.
    /// </summary>
    public class KeyHoldTracker
    {
        /// <summary>How long the key must be held, in seconds.</summary>
        public float HoldSeconds = 2f;

        bool m_Down;
        bool m_Held;
        float m_DownFor;

        /// <summary>True while the key is down.</summary>
        public bool IsDown => m_Down;

        /// <summary>How far into a hold the key is, 0 to 1 (0 when up).</summary>
        public float Progress => m_Down && !m_Held ? m_DownFor / HoldSeconds : 0f;

        /// <summary>Feeds the key state each frame.</summary>
        public KeyHoldEvent Update(bool down, float deltaTime)
        {
            if (down)
            {
                if (!m_Down)
                {
                    m_Down = true;
                    m_Held = false;
                    m_DownFor = 0f;
                    return KeyHoldEvent.None;
                }

                m_DownFor += deltaTime;
                if (m_Held || m_DownFor < HoldSeconds)
                    return KeyHoldEvent.None;
                m_Held = true;
                return KeyHoldEvent.Hold;
            }

            if (!m_Down)
                return KeyHoldEvent.None;
            m_Down = false;
            return m_Held ? KeyHoldEvent.None : KeyHoldEvent.Press;
        }
    }
}
