using System;
using System.Collections.Generic;
using AIEdge.UI;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace AIEdge.TinyGarden
{
    /// <summary>
    /// The tabletop planter: a 3×3 grid of plots with low-poly plants built from primitives, finger pointing to pick
    /// plots, and hand play (pour the watering can, drop a seed packet on a plot, pull a flower to harvest it).
    /// Row 1 (plots 1-3) is the far row, as "top row" reads when looking down at a table.
    /// </summary>
    public class GardenView : MonoBehaviour
    {
        public const float PlotSize = 0.12f;
        public const float PlotPitch = 0.13f;
        const float k_BaseHeight = 0.06f;
        const float k_SoilTop = 0.07f;
        const float k_PointDwell = 0.35f;
        const float k_PointRange = 2.5f;
        const float k_PourSeconds = 0.6f;
        const float k_PacketDropHeight = 0.3f;
        const float k_HarvestPull = 0.08f;

        static readonly Color k_Wood = new Color(0.55f, 0.36f, 0.2f);
        static readonly Color k_DrySoil = new Color(0.52f, 0.38f, 0.26f);
        static readonly Color k_WetSoil = new Color(0.27f, 0.18f, 0.11f);
        static readonly Color k_Stem = new Color(0.3f, 0.62f, 0.25f);
        static readonly Color k_Droopy = new Color(0.6f, 0.62f, 0.3f);
        static readonly Color k_Selected = new Color(0.3f, 0.6f, 1f);
        static readonly Color k_Hovered = new Color(0.85f, 0.9f, 1f);
        static readonly Color k_DropTarget = new Color(0.4f, 0.95f, 0.35f);
        // Pale, so the seeds stand out on dry and wet soil alike.
        static readonly Color k_SeedColor = new Color(0.95f, 0.88f, 0.62f);
        const int k_SpilledSeeds = 24;
        const float k_TiltSeconds = 0.2f;
        const float k_SeedPourSeconds = 0.8f;
        const float k_SeedRestSeconds = 1.2f;
        static MaterialPropertyBlock s_Block;
        static Material s_Material;

        /// <summary>Raised when the hands change the garden: a description and the per-plot results.</summary>
        public event Action<string, List<GardenEvent>> HandAction;

        /// <summary>Raised when the pointed-at selection changes.</summary>
        public event Action SelectionChanged;

        /// <summary>The game state shown by this view.</summary>
        public GardenState State { get; private set; }

        /// <summary>Plots picked by pointing, used for "here" and "these".</summary>
        public IReadOnlyCollection<int> Selection => m_Selection;

        /// <summary>Seeds shown falling from packets so far (for tests).</summary>
        public int SeedsSpilled { get; private set; }

        /// <summary>The plot a held seed packet would be planted in if let go now (lit green), or 0.</summary>
        public int DropTarget => m_DropTarget;

        class PlotView
        {
            public Renderer Soil;
            public Renderer Rim;
            public Transform PlantRoot;
            public PlotStage Stage = (PlotStage)(-1);
            public Seed Seed;
            public bool Wet;
        }

        readonly PlotView[] m_Plots = new PlotView[GardenState.PlotCount];
        readonly SortedSet<int> m_Selection = new SortedSet<int>();
        readonly List<XRHandSubsystem> m_Subsystems = new List<XRHandSubsystem>();
        readonly float[] m_PourTime = new float[GardenState.PlotCount + 1];
        XRHandSubsystem m_Hands;
        int m_Hovered;
        int m_DropTarget;
        readonly List<(Transform packet, Vector3 home)> m_Pouring = new List<(Transform, Vector3)>();
        readonly List<(XRGrabInteractable grab, Transform packet)> m_Packets = new List<(XRGrabInteractable, Transform)>();
        float m_HoverTime;
        Transform m_Can;
        Transform m_Spout;
        XRGrabInteractable m_CanGrab;
        float m_NextDrop;

        /// <summary>Builds the planter for a game state.</summary>
        /// <param name="state">Game state to show.</param>
        /// <param name="material">Lit material for all pieces, tinted per piece. It must be a referenced asset so its
        /// shader is in the build: primitives' default material renders magenta on device.</param>
        public void Initialize(GardenState state, Material material)
        {
            s_Material = material;
            State = state;
            State.Changed += Refresh;
            BuildPlanter();
            BuildWateringCan();
            BuildSeedPackets();
            Refresh();
        }

        void OnDestroy()
        {
            if (State != null)
                State.Changed -= Refresh;
        }

        /// <summary>Places the planter on a surface point, its near edge facing the user.</summary>
        public void Place(Vector3 surfacePoint, Vector3 towardUser)
        {
            var forward = Vector3.ProjectOnPlane(-towardUser, Vector3.up);
            if (forward.sqrMagnitude < 1e-4f)
                forward = Vector3.forward;
            transform.SetPositionAndRotation(surfacePoint, Quaternion.LookRotation(forward, Vector3.up));
        }

        /// <summary>Replaces the pointed-at selection (also used by tests).</summary>
        public void SetSelection(IEnumerable<int> plots)
        {
            m_Selection.Clear();
            foreach (var plot in plots)
                if (plot >= 1 && plot <= GardenState.PlotCount)
                    m_Selection.Add(plot);
            RefreshHighlights();
            SelectionChanged?.Invoke();
        }

        /// <summary>Clears the pointed-at selection.</summary>
        public void ClearSelection() => SetSelection(Array.Empty<int>());

        /// <summary>Local position of a plot's soil surface center.</summary>
        public static Vector3 PlotLocalPosition(int number)
        {
            var row = (number - 1) / 3;
            var column = (number - 1) % 3;
            return new Vector3((column - 1) * PlotPitch, k_SoilTop, (1 - row) * PlotPitch);
        }

        /// <summary>
        /// The plot a seed packet at a local position plants in when let go: over a plot and less than 30 cm above the
        /// soil; else 0.
        /// </summary>
        public static int DropPlot(Vector3 local) => local.y < k_SoilTop + k_PacketDropHeight ? PlotAtLocal(local) : 0;

        /// <summary>The plot under a local position on the planter, or 0.</summary>
        public static int PlotAtLocal(Vector3 local)
        {
            var column = Mathf.RoundToInt(local.x / PlotPitch) + 1;
            var row = 1 - Mathf.RoundToInt(local.z / PlotPitch);
            if (column < 0 || column > 2 || row < 0 || row > 2)
                return 0;
            var center = PlotLocalPosition(row * 3 + column + 1);
            var half = PlotSize / 2f + 0.005f;
            return Mathf.Abs(local.x - center.x) <= half && Mathf.Abs(local.z - center.z) <= half ? row * 3 + column + 1 : 0;
        }

        void Update()
        {
            State?.Tick(Time.deltaTime);
            UpdatePointing();
            UpdatePouring();
            UpdateDropTarget();
        }

        // ---- Pointing -------------------------------------------------------------------------------------------

        void UpdatePointing()
        {
            if (m_Hands == null || !m_Hands.running)
            {
                m_Subsystems.Clear();
                SubsystemManager.GetSubsystems(m_Subsystems);
                m_Hands = m_Subsystems.Find(s => s.running);
            }

            var pointed = 0;
            if (m_Hands != null)
            {
                pointed = PointedPlot(m_Hands.rightHand);
                if (pointed == 0)
                    pointed = PointedPlot(m_Hands.leftHand);
            }

            if (pointed != m_Hovered)
            {
                m_Hovered = pointed;
                m_HoverTime = 0f;
                RefreshHighlights();
            }
            else if (pointed != 0 && !m_Selection.Contains(pointed))
            {
                m_HoverTime += Time.deltaTime;
                if (m_HoverTime >= k_PointDwell)
                {
                    m_Selection.Add(pointed);
                    RefreshHighlights();
                    SelectionChanged?.Invoke();
                }
            }
        }

        // The plot an extended index finger points at, intersecting its ray with the soil plane. Only counts in a
        // pointing pose (index out, middle finger curled), so a relaxed or pinching hand doesn't select.
        int PointedPlot(XRHand hand)
        {
            if (!hand.isTracked ||
                !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var tip) ||
                !hand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out var knuckle) ||
                !hand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out var middle) ||
                !hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm))
                return 0;

            if (Vector3.Distance(tip.position, palm.position) < 0.07f || Vector3.Distance(middle.position, palm.position) > 0.06f)
                return 0;

            var trackingSpace = Camera.main != null ? Camera.main.transform.parent : null;
            var origin = trackingSpace != null ? trackingSpace.TransformPoint(tip.position) : tip.position;
            var back = trackingSpace != null ? trackingSpace.TransformPoint(knuckle.position) : knuckle.position;
            var localOrigin = transform.InverseTransformPoint(origin);
            var localDirection = transform.InverseTransformDirection((origin - back).normalized);
            if (localDirection.y > -0.05f)
                return 0;

            var distance = (localOrigin.y - k_SoilTop) / -localDirection.y;
            if (distance < 0f || distance > k_PointRange)
                return 0;
            return PlotAtLocal(localOrigin + localDirection * distance);
        }

        // ---- Hand play ------------------------------------------------------------------------------------------

        void UpdatePouring()
        {
            if (m_CanGrab == null || !m_CanGrab.isSelected)
                return;

            // Pouring when the spout points well below horizontal.
            if (Vector3.Dot(m_Spout.forward, Vector3.down) < 0.35f)
                return;

            if (Time.time >= m_NextDrop)
            {
                m_NextDrop = Time.time + 0.06f;
                SpawnDrop(m_Spout.position + m_Spout.forward * 0.03f);
            }

            var local = transform.InverseTransformPoint(m_Spout.position);
            if (local.y < k_SoilTop || local.y > k_SoilTop + 0.4f)
                return;
            var plot = PlotAtLocal(local);
            if (plot == 0)
                return;

            m_PourTime[plot] += Time.deltaTime;
            if (m_PourTime[plot] >= k_PourSeconds)
            {
                m_PourTime[plot] = float.NegativeInfinity; // once per pour
                HandAction?.Invoke($"poured water on plot {plot}", State.Water(new[] { plot }));
            }
        }

        void OnCanReleased(SelectExitEventArgs args)
        {
            for (var i = 0; i < m_PourTime.Length; i++)
                m_PourTime[i] = 0f;
        }

        void SpawnDrop(Vector3 position)
        {
            var drop = Primitive(PrimitiveType.Sphere, null, position, Vector3.one * 0.012f, new Color(0.45f, 0.7f, 1f));
            var body = drop.AddComponent<Rigidbody>();
            body.linearVelocity = m_Spout.forward * 0.3f;
            body.detectCollisions = false;
            Destroy(drop, 0.7f);
        }

        // While a seed packet is held, the plot it would be planted in lights up.
        void UpdateDropTarget()
        {
            var target = 0;
            foreach (var (grab, packet) in m_Packets)
                if (grab.isSelected)
                    target = DropPlot(transform.InverseTransformPoint(packet.position));
            if (target == m_DropTarget)
                return;
            m_DropTarget = target;
            RefreshHighlights();
        }

        void OnPacketReleased(Transform packet, Seed seed, Vector3 home)
        {
            var local = transform.InverseTransformPoint(packet.position);
            var plot = DropPlot(local);
            var moved = Vector3.Distance(local, home);
            Debug.Log($"[AIEdge] Garden: let go of the {GardenState.Name(seed)} packet {(local.y - k_SoilTop) * 100f:0} cm above the soil, plot {plot}");
            if (plot != 0)
                StartCoroutine(PourPacket(packet, home));
            else
                ReturnPacket(packet, home);
            m_DropTarget = 0;
            RefreshHighlights();
            if (plot != 0)
                HandAction?.Invoke($"dropped a {GardenState.Name(seed)} packet on plot {plot}", State.Plant(seed, new[] { plot }));
            else if (moved > 0.05f)
                HandAction?.Invoke($"let go of the {GardenState.Name(seed)} packet away from the plots",
                    new List<GardenEvent> { new GardenEvent { message = "hold it over a plot until the plot lights up green, then let go" } });
        }

        static void ReturnPacket(Transform packet, Vector3 home)
        {
            packet.localPosition = home;
            packet.localRotation = Quaternion.identity;
        }

        // The packet stays where it was let go, tips over and pours its seeds onto the soil, then goes back beside the
        // planter. It can't be grabbed while pouring.
        System.Collections.IEnumerator PourPacket(Transform packet, Vector3 home)
        {
            var grab = packet.GetComponent<XRGrabInteractable>();
            m_Pouring.Add((packet, home));
            if (grab != null)
                grab.enabled = false;

            // Tip the top of the packet down, towards the side it already leans to.
            var start = packet.rotation;
            var lean = Vector3.ProjectOnPlane(packet.up, transform.up);
            var axis = Vector3.Cross(transform.up, lean.sqrMagnitude > 1e-4f ? lean : transform.forward).normalized;
            var end = Quaternion.AngleAxis(130f, axis) * Quaternion.FromToRotation(packet.up, transform.up) * start;
            for (var t = 0f; t < k_TiltSeconds; t += Time.deltaTime)
            {
                packet.rotation = Quaternion.Slerp(start, end, t / k_TiltSeconds);
                yield return null;
            }
            packet.rotation = end;

            for (var i = 0; i < k_SpilledSeeds; i++)
            {
                // From the opening at the top of the packet, now pointing down.
                SpillSeed(packet.TransformPoint(new Vector3(0f, 0.045f, 0f)), packet.up);
                if (i % 3 == 2)
                    yield return new WaitForSeconds(k_SeedPourSeconds * 3f / k_SpilledSeeds);
            }
            yield return new WaitForSeconds(0.15f);

            m_Pouring.RemoveAll(p => p.packet == packet);
            ReturnPacket(packet, home);
            if (grab != null)
                grab.enabled = true;
        }

        void SpillSeed(Vector3 opening, Vector3 spout)
        {
            var jitter = UnityEngine.Random.insideUnitCircle * 0.01f;
            var position = opening + transform.right * jitter.x + transform.forward * jitter.y;
            var seed = Primitive(PrimitiveType.Sphere, null, position, new Vector3(0.011f, 0.007f, 0.011f) * transform.lossyScale.y, k_SeedColor);
            seed.transform.rotation = UnityEngine.Random.rotation;
            var body = seed.AddComponent<Rigidbody>();
            body.detectCollisions = false;
            var spread = UnityEngine.Random.insideUnitCircle * 0.05f;
            body.linearVelocity = spout * 0.15f + transform.right * spread.x + transform.forward * spread.y;
            body.angularVelocity = UnityEngine.Random.insideUnitSphere * 8f;
            SeedsSpilled++;
            StartCoroutine(SettleSeed(seed, body));
        }

        // A falling seed comes to rest on the soil, stays a moment, then sinks into it.
        System.Collections.IEnumerator SettleSeed(GameObject seed, Rigidbody body)
        {
            Destroy(seed, 4f);
            var soil = k_SoilTop + 0.003f;
            while (seed != null && transform.InverseTransformPoint(seed.transform.position).y > soil)
                yield return null;
            if (seed == null)
                yield break;
            body.isKinematic = true;
            var local = transform.InverseTransformPoint(seed.transform.position);
            seed.transform.position = transform.TransformPoint(new Vector3(local.x, soil, local.z));
            yield return new WaitForSeconds(k_SeedRestSeconds);
            var size = seed.transform.localScale;
            for (var t = 0f; t < 0.4f && seed != null; t += Time.deltaTime)
            {
                seed.transform.localScale = size * (1f - t / 0.4f);
                yield return null;
            }
            if (seed != null)
                Destroy(seed);
        }

        void OnDisable()
        {
            // Pouring stops with the view: put any tipped packet back.
            foreach (var (packet, home) in m_Pouring)
                if (packet != null)
                {
                    ReturnPacket(packet, home);
                    var grab = packet.GetComponent<XRGrabInteractable>();
                    if (grab != null)
                        grab.enabled = true;
                }
            m_Pouring.Clear();
        }

        void OnFlowerPulled(int plot, Transform flower)
        {
            var seed = State[plot].seed;
            flower.SetParent(null, true);
            Destroy(flower.gameObject, 0.5f);
            HandAction?.Invoke($"picked the {GardenState.Name(seed)} in plot {plot}", State.Harvest(new[] { plot }));
        }

        // ---- Visuals --------------------------------------------------------------------------------------------

        void Refresh()
        {
            if (State == null || m_Plots[0] == null)
                return;

            for (var number = 1; number <= GardenState.PlotCount; number++)
            {
                var plot = State[number];
                var view = m_Plots[number - 1];
                if (view.Wet != plot.IsWet || view.Stage != plot.stage)
                    SetColor(view.Soil, plot.IsWet ? k_WetSoil : k_DrySoil);
                if (view.Stage != plot.stage || view.Seed != plot.seed)
                    BuildPlant(number, view, plot);
                view.Stage = plot.stage;
                view.Seed = plot.seed;
                view.Wet = plot.IsWet;
            }
        }

        void RefreshHighlights()
        {
            for (var number = 1; number <= GardenState.PlotCount; number++)
            {
                var color = number == m_DropTarget ? k_DropTarget : m_Selection.Contains(number) ? k_Selected :
                    number == m_Hovered ? k_Hovered : k_Wood * 0.8f;
                SetColor(m_Plots[number - 1].Rim, color);
            }
        }

        void BuildPlant(int number, PlotView view, Plot plot)
        {
            foreach (Transform child in view.PlantRoot)
                Destroy(child.gameObject);

            var root = view.PlantRoot;
            var flowerColor = FlowerColor(plot.seed);
            switch (plot.stage)
            {
                case PlotStage.Seed:
                    Primitive(PrimitiveType.Sphere, root, new Vector3(0f, 0.005f, 0f), new Vector3(0.05f, 0.02f, 0.05f), k_WetSoil * 1.3f);
                    Primitive(PrimitiveType.Cube, root, new Vector3(0.035f, 0.03f, -0.035f), new Vector3(0.002f, 0.06f, 0.002f), k_Wood);
                    Primitive(PrimitiveType.Cube, root, new Vector3(0.035f, 0.06f, -0.035f), new Vector3(0.025f, 0.018f, 0.003f), flowerColor);
                    break;
                case PlotStage.Sprout:
                case PlotStage.Thirsty:
                    var thirsty = plot.stage == PlotStage.Thirsty;
                    var stem = Primitive(PrimitiveType.Capsule, root, new Vector3(0f, 0.025f, 0f), new Vector3(0.012f, 0.025f, 0.012f), thirsty ? k_Droopy : k_Stem);
                    if (thirsty)
                        stem.transform.localRotation = Quaternion.Euler(0f, 0f, 35f);
                    Primitive(PrimitiveType.Sphere, root, new Vector3(thirsty ? -0.02f : 0.012f, 0.045f, 0f), new Vector3(0.025f, 0.006f, 0.014f), thirsty ? k_Droopy : k_Stem);
                    break;
                case PlotStage.Bud:
                    Primitive(PrimitiveType.Capsule, root, new Vector3(0f, 0.045f, 0f), new Vector3(0.012f, 0.045f, 0.012f), k_Stem);
                    Primitive(PrimitiveType.Sphere, root, new Vector3(0f, 0.095f, 0f), Vector3.one * 0.025f, Color.Lerp(k_Stem, flowerColor, 0.5f));
                    break;
                case PlotStage.Flower:
                    Primitive(PrimitiveType.Capsule, root, new Vector3(0f, 0.055f, 0f), new Vector3(0.012f, 0.055f, 0.012f), k_Stem);
                    Primitive(PrimitiveType.Sphere, root, new Vector3(0.018f, 0.05f, 0f), new Vector3(0.03f, 0.006f, 0.016f), k_Stem);
                    BuildBloom(number, root, plot.seed, flowerColor);
                    break;
            }
        }

        // The flower head is grabbable: pulling it up harvests the plot.
        void BuildBloom(int number, Transform root, Seed seed, Color color)
        {
            var head = new GameObject("Bloom");
            head.SetActive(false);
            head.transform.SetParent(root, false);
            head.transform.localPosition = new Vector3(0f, 0.115f, 0f);
            var collider = head.AddComponent<SphereCollider>();
            collider.radius = 0.035f;

            switch (seed)
            {
                case Seed.Sunflower:
                    Primitive(PrimitiveType.Cylinder, head.transform, Vector3.zero, new Vector3(0.07f, 0.004f, 0.07f), color).transform.localRotation = Quaternion.Euler(70f, 0f, 0f);
                    Primitive(PrimitiveType.Cylinder, head.transform, new Vector3(0f, 0.002f, -0.004f), new Vector3(0.03f, 0.005f, 0.03f), new Color(0.35f, 0.2f, 0.08f)).transform.localRotation = Quaternion.Euler(70f, 0f, 0f);
                    break;
                case Seed.Daisy:
                    Primitive(PrimitiveType.Cylinder, head.transform, Vector3.zero, new Vector3(0.05f, 0.003f, 0.05f), color);
                    Primitive(PrimitiveType.Sphere, head.transform, new Vector3(0f, 0.004f, 0f), new Vector3(0.018f, 0.01f, 0.018f), new Color(1f, 0.85f, 0.2f));
                    break;
                case Seed.Rose:
                    Primitive(PrimitiveType.Sphere, head.transform, Vector3.zero, new Vector3(0.04f, 0.035f, 0.04f), color);
                    Primitive(PrimitiveType.Sphere, head.transform, new Vector3(0f, 0.012f, 0f), new Vector3(0.025f, 0.02f, 0.025f), color * 0.8f);
                    break;
                default:
                    var gem = Primitive(PrimitiveType.Cube, head.transform, Vector3.zero, Vector3.one * 0.035f, color);
                    gem.transform.localRotation = Quaternion.Euler(45f, 45f, 0f);
                    gem.AddComponent<Spin>();
                    break;
            }

            var body = head.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            var grab = head.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.useDynamicAttach = true;
            grab.throwOnDetach = false;
            var rest = head.transform.localPosition;
            var harvested = false;
            head.SetActive(true);

            grab.selectExited.AddListener(_ =>
            {
                if (!harvested && head != null)
                    head.transform.localPosition = rest;
            });
            head.AddComponent<PullWatcher>().Initialize(rest, k_HarvestPull, () =>
            {
                harvested = true;
                OnFlowerPulled(number, head.transform);
            });
        }

        static Color FlowerColor(Seed seed)
        {
            switch (seed)
            {
                case Seed.Sunflower: return new Color(1f, 0.8f, 0.1f);
                case Seed.Daisy: return new Color(0.97f, 0.97f, 0.95f);
                case Seed.Rose: return new Color(0.85f, 0.1f, 0.2f);
                default: return new Color(0.7f, 0.3f, 1f);
            }
        }

        void BuildPlanter()
        {
            Primitive(PrimitiveType.Cube, transform, new Vector3(0f, k_BaseHeight / 2f, 0f), new Vector3(0.42f, k_BaseHeight, 0.42f), k_Wood);

            var labels = UiFactory.CreatePanel(transform, "PlotNumbers", new Vector2(420f, 420f));
            Destroy(labels.GetComponent<TrackedDeviceGraphicRaycaster>());
            labels.Find("Background").gameObject.SetActive(false);
            labels.localPosition = new Vector3(0f, k_SoilTop + 0.001f, 0f);
            labels.localRotation = Quaternion.Euler(90f, 0f, 0f);

            for (var number = 1; number <= GardenState.PlotCount; number++)
            {
                var center = PlotLocalPosition(number);
                var view = new PlotView();
                view.Rim = Primitive(PrimitiveType.Cube, transform, new Vector3(center.x, k_SoilTop - 0.006f, center.z), new Vector3(PlotSize + 0.008f, 0.008f, PlotSize + 0.008f), k_Wood * 0.8f).GetComponent<Renderer>();
                view.Soil = Primitive(PrimitiveType.Cube, transform, new Vector3(center.x, k_SoilTop - 0.004f, center.z), new Vector3(PlotSize, 0.01f, PlotSize), k_DrySoil).GetComponent<Renderer>();
                view.PlantRoot = new GameObject($"Plot{number}").transform;
                view.PlantRoot.SetParent(transform, false);
                view.PlantRoot.localPosition = center;
                m_Plots[number - 1] = view;

                var label = UiFactory.CreateText(labels, $"Number{number}", number.ToString(), 16, TextAlignmentOptions.Center);
                label.color = new Color(1f, 1f, 1f, 0.75f);
                label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                label.rectTransform.sizeDelta = new Vector2(30f, 20f);
                // Canvas is rotated flat: canvas y maps to planter z. Labels sit at each plot's near-left corner.
                label.rectTransform.anchoredPosition = new Vector2(center.x * 1000f - 48f, center.z * 1000f - 50f);
            }

            RefreshHighlights();
        }

        void BuildWateringCan()
        {
            var can = new GameObject("WateringCan");
            can.SetActive(false);
            m_Can = can.transform;
            m_Can.SetParent(transform, false);
            m_Can.localPosition = new Vector3(-0.3f, 0.06f, -0.05f);
            var color = new Color(0.25f, 0.55f, 0.75f);
            Primitive(PrimitiveType.Cylinder, m_Can, Vector3.zero, new Vector3(0.08f, 0.055f, 0.08f), color);
            var spoutArm = Primitive(PrimitiveType.Cylinder, m_Can, new Vector3(0f, 0.03f, 0.065f), new Vector3(0.012f, 0.045f, 0.012f), color);
            spoutArm.transform.localRotation = Quaternion.Euler(55f, 0f, 0f);
            Primitive(PrimitiveType.Cube, m_Can, new Vector3(0f, 0.07f, -0.02f), new Vector3(0.012f, 0.012f, 0.07f), color * 0.8f);
            m_Spout = new GameObject("Spout").transform;
            m_Spout.SetParent(m_Can, false);
            m_Spout.localPosition = new Vector3(0f, 0.065f, 0.095f);
            m_Spout.localRotation = Quaternion.Euler(-10f, 0f, 0f); // forward: out of the spout

            var collider = can.AddComponent<BoxCollider>();
            collider.size = new Vector3(0.09f, 0.13f, 0.17f);
            collider.center = new Vector3(0f, 0.01f, 0.02f);
            var body = can.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            m_CanGrab = can.AddComponent<XRGrabInteractable>();
            m_CanGrab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            m_CanGrab.useDynamicAttach = true;
            m_CanGrab.throwOnDetach = false;
            // Grabbed with the ray from afar, it stays out there (so a planter beyond reach can still be watered).
            m_CanGrab.farAttachMode = InteractableFarAttachMode.Far;
            m_CanGrab.selectExited.AddListener(OnCanReleased);
            can.SetActive(true);
        }

        void BuildSeedPackets()
        {
            var seeds = new[] { Seed.Sunflower, Seed.Daisy, Seed.Rose, Seed.Special };
            for (var i = 0; i < seeds.Length; i++)
            {
                var seed = seeds[i];
                var packet = new GameObject($"{seed}Packet");
                packet.SetActive(false);
                packet.transform.SetParent(transform, false);
                var home = new Vector3(0.28f, 0.04f, 0.15f - i * 0.09f);
                packet.transform.localPosition = home;
                Primitive(PrimitiveType.Cube, packet.transform, Vector3.zero, new Vector3(0.06f, 0.08f, 0.01f), Color.Lerp(FlowerColor(seed), Color.white, 0.2f));
                Primitive(PrimitiveType.Cube, packet.transform, new Vector3(0f, 0.025f, -0.006f), new Vector3(0.05f, 0.02f, 0.002f), new Color(0.95f, 0.92f, 0.85f));

                var collider = packet.AddComponent<BoxCollider>();
                collider.size = new Vector3(0.07f, 0.09f, 0.03f);
                var body = packet.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                var grab = packet.AddComponent<XRGrabInteractable>();
                grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
                grab.useDynamicAttach = true;
                grab.throwOnDetach = false;
                // Grabbed with the ray from afar, the packet stays at the end of the ray instead of jumping to the hand,
                // so it can be swept over the plots of a planter beyond reach. Grabbed up close, it's in the hand.
                grab.farAttachMode = InteractableFarAttachMode.Far;
                grab.selectEntered.AddListener(args =>
                    Debug.Log($"[AIEdge] Garden: {args.interactorObject.transform.name} on {args.interactorObject.transform.parent?.name} grabbed the {GardenState.Name(seed)} packet"));
                grab.selectExited.AddListener(_ => OnPacketReleased(packet.transform, seed, home));
                m_Packets.Add((grab, packet.transform));
                packet.SetActive(true);
            }
        }

        static GameObject Primitive(PrimitiveType type, Transform parent, Vector3 localPosition, Vector3 localScale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            // Decoration only; interaction uses explicit colliders.
            DestroyImmediate(go.GetComponent<Collider>());
            if (parent != null)
                go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            if (s_Material != null)
                go.GetComponent<Renderer>().sharedMaterial = s_Material;
            SetColor(go.GetComponent<Renderer>(), color);
            return go;
        }

        static void SetColor(Renderer renderer, Color color)
        {
            s_Block ??= new MaterialPropertyBlock();
            s_Block.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(s_Block);
        }

        /// <summary>Slowly spins the special flower.</summary>
        class Spin : MonoBehaviour
        {
            void Update() => transform.Rotate(0f, 60f * Time.deltaTime, 0f, Space.World);
        }

        /// <summary>Calls back once when a grabbed object is pulled a distance from its rest position.</summary>
        class PullWatcher : MonoBehaviour
        {
            Vector3 m_Rest;
            float m_Distance;
            Action m_Pulled;

            public void Initialize(Vector3 rest, float distance, Action pulled)
            {
                m_Rest = rest;
                m_Distance = distance;
                m_Pulled = pulled;
            }

            void Update()
            {
                if (m_Pulled == null || Vector3.Distance(transform.localPosition, m_Rest) < m_Distance)
                    return;

                var pulled = m_Pulled;
                m_Pulled = null;
                pulled();
            }
        }
    }
}
