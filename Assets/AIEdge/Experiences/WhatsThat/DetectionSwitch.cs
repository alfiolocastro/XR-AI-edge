using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace AIEdge.WhatsThat
{
    /// <summary>
    /// Turns person detection on and off. Detection starts off at every launch. Holding the headset's volume-up key
    /// for <see cref="HoldSeconds"/> arms it: nothing shows or sounds for <see cref="ArmSeconds"/>, then detection
    /// starts and the targeting display appears all at once. Holding it again (or during the pause) turns it off. A short press still raises the volume. The People
    /// button in What's That? does the same as a hold.
    /// </summary>
    public class DetectionSwitch : MonoBehaviour
    {
        const string k_BridgeClass = "com.xraiedge.litertlm.KeyBridge";

        /// <summary>Where the switch is.</summary>
        public enum Mode
        {
            Off,
            Arming,
            On,
        }

        /// <summary>Seconds the volume-up key must be held.</summary>
        public const float HoldSeconds = 2f;

        /// <summary>Seconds between the hold and detection starting.</summary>
        public const float ArmSeconds = 5f;

        /// <summary>The switch, once the app has created it.</summary>
        public static DetectionSwitch Instance { get; private set; }

        /// <summary>Raised when <see cref="Current"/> changes.</summary>
        public event Action Changed;

        /// <summary>Where the switch is now.</summary>
        public Mode Current { get; private set; }

        /// <summary>Seconds left before detection starts, while arming.</summary>
        public float ArmingLeft => Current == Mode.Arming ? Mathf.Max(0f, m_ArmAt - Time.unscaledTime) : 0f;

        /// <summary>How far the volume-up key is into a hold, 0 to 1.</summary>
        public float HoldProgress => m_Hold.Progress;

        /// <summary>True when the volume-up key is read by the app (on the headset).</summary>
        public bool HasKey => m_Bridge != null;

        /// <summary>Volume-up presses and releases received so far (for tests).</summary>
        public int KeyEvents { get; private set; }

        readonly KeyHoldTracker m_Hold = new KeyHoldTracker { HoldSeconds = HoldSeconds };
        readonly ConcurrentQueue<bool> m_KeyQueue = new ConcurrentQueue<bool>();
        AndroidJavaObject m_Bridge;
        bool m_KeyDown;
        float m_ArmAt;

        void Awake()
        {
            Instance = this;
            PersonDetector.Enabled = false;
        }

        void Start()
        {
            if (Application.platform != RuntimePlatform.Android)
                return;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_Bridge = new AndroidJavaObject(k_BridgeClass, activity, new ListenerProxy(this));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AIEdge] Volume key unavailable: {e.Message}");
            }
        }

        void OnDestroy()
        {
            m_Bridge?.Call("close");
            m_Bridge?.Dispose();
            m_Bridge = null;
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            // Each press and release counts, even when both arrive within one frame.
            while (m_KeyQueue.TryDequeue(out var down))
            {
                KeyEvents++;
                m_KeyDown = down;
                Handle(m_Hold.Update(down, 0f));
            }

            if (m_KeyDown)
                Handle(m_Hold.Update(true, Time.unscaledDeltaTime));

            if (Current == Mode.Arming && Time.unscaledTime >= m_ArmAt)
                Set(Mode.On);
        }

        void Handle(KeyHoldEvent e)
        {
            if (e == KeyHoldEvent.Hold)
                Toggle();
            else if (e == KeyHoldEvent.Press)
                m_Bridge?.Call("raiseVolume");
        }

        /// <summary>Arms detection when off; turns it off when arming or on.</summary>
        public void Toggle()
        {
            if (Current == Mode.Off)
            {
                m_ArmAt = Time.unscaledTime + ArmSeconds;
                Set(Mode.Arming);
            }
            else
            {
                Set(Mode.Off);
            }
        }

        /// <summary>Simulates the volume-up key going down or up (for tests).</summary>
        public void SimulateKey(bool down) => m_KeyQueue.Enqueue(down);

        void Set(Mode mode)
        {
            if (Current == mode)
                return;
            Current = mode;
            PersonDetector.Enabled = mode == Mode.On;
            Debug.Log($"[AIEdge] Person detection: {mode}");
            Changed?.Invoke();
        }

        class ListenerProxy : AndroidJavaProxy
        {
            readonly DetectionSwitch m_Owner;

            public ListenerProxy(DetectionSwitch owner) : base(k_BridgeClass + "$Listener") => m_Owner = owner;

            public void onVolumeUp(bool down) => m_Owner.m_KeyQueue.Enqueue(down);
        }
    }
}
