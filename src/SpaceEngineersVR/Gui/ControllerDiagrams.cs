using System;
using System.Collections.Generic;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>One controller's drawing: simplified shapes of its body and controls on a 0 to 1 canvas, and where each control sits.</summary>
    internal sealed class ControllerDiagram
    {
        /// <summary>How a shape is drawn: the body's outline is heaviest, a control's is lighter, a hint (a thumbrest, a pad's cross) is faint.</summary>
        internal enum ShapeStyle { Body, Part, Faint }

        /// <summary>One drawn piece: closed rings (filled by the even-odd rule, so a ring inside a ring is a hole) or an open line.</summary>
        internal sealed class Shape
        {
            public List<Vector2[]> Rings = new List<Vector2[]>();
            public bool Closed = true;
            public bool Fill, Stroke;
            public ShapeStyle Style;
            /// <summary>The control this piece lights up for, if any.</summary>
            public ControllerControl? Tag;
            /// <summary>Drawn only while its control is highlighted (a trackpad zone has no outline of its own).</summary>
            public bool HighlightOnly;
        }

        /// <summary>The controller's name as the player knows it.</summary>
        public string Name { get; internal set; }

        /// <summary>Each control the controller has: the centre of its drawing on the 0 to 1 canvas and the radius of its mark, where a label line or highlight goes.</summary>
        public IReadOnlyList<(ControllerControl Control, Vector2 At, float Radius)> Anchors => AnchorList;

        internal readonly List<(ControllerControl Control, Vector2 At, float Radius)> AnchorList = new List<(ControllerControl, Vector2, float)>();
        internal readonly List<Shape> Shapes = new List<Shape>();
    }

    /// <summary>
    /// Vector drawings of the VR controllers, one per OpenXR interaction profile and hand, with the anchors of the controls each
    /// has, and a small rasterizer that draws one into RGBA pixels in the game's menu colours. Nothing is shipped as an image and
    /// nothing here uses System.Drawing. The drawings are our own simplified silhouettes seen face-on from the thumb side: the
    /// trigger and grip show as tabs on the finger side. Which controls exist follows the OpenXR specification's input list for
    /// each profile.
    /// </summary>
    internal static class ControllerDiagrams
    {
        private delegate void Drawing(Sketch s, Hand hand);

        private static readonly Dictionary<string, (string Name, Drawing Draw)> Profiles = new Dictionary<string, (string, Drawing)>
        {
            ["oculus/touch_controller"] = ("Meta Quest Touch", (s, h) => Ringed(s, h, TouchLike.Quest)),
            ["valve/index_controller"] = ("Valve Index", Index),
            ["htc/vive_controller"] = ("HTC Vive wand", ViveWand),
            ["microsoft/motion_controller"] = ("Windows Mixed Reality", WindowsMixedReality),
            ["hp/mixed_reality_controller"] = ("HP Reverb G2", (s, h) => Ringed(s, h, TouchLike.ReverbG2)),
            ["bytedance/pico4_controller"] = ("PICO 4", (s, h) => Ringed(s, h, TouchLike.Pico4)),
            ["bytedance/pico_neo3_controller"] = ("PICO Neo3", (s, h) => Ringed(s, h, TouchLike.PicoNeo3)),
            ["bytedance/pico_ultra_controller_bd"] = ("PICO 4 Ultra", (s, h) => Ringed(s, h, TouchLike.PicoUltra)),
            ["htc/vive_cosmos_controller"] = ("HTC Vive Cosmos", (s, h) => Ringed(s, h, TouchLike.Cosmos)),
            ["htc/vive_focus3_controller"] = ("HTC Vive Focus 3", (s, h) => Ringed(s, h, TouchLike.Focus3)),
            ["valve/frame_controller_valve"] = ("Steam Frame", SteamFrame),
            ["khr/simple_controller"] = ("Generic controller", Simple),
        };

        private static readonly Dictionary<string, ControllerDiagram> Cache = new Dictionary<string, ControllerDiagram>();

        /// <summary>The controller's name for a profile (without "/interaction_profiles/"), or null for one not drawn here.</summary>
        public static string Title(string profile) => profile != null && Profiles.TryGetValue(profile, out var entry) ? entry.Name : null;

        /// <summary>The profile paths with a drawing, without "/interaction_profiles/".</summary>
        public static IEnumerable<string> KnownProfiles => Profiles.Keys;

        /// <summary>The drawing for an interaction profile (with or without its "/interaction_profiles/" prefix) and hand, if there is one.</summary>
        public static bool TryGet(string profile, Hand hand, out ControllerDiagram d)
        {
            d = null;
            profile = OpenXR.InteractionProfile.Key(profile);
            if (profile == null || !Profiles.TryGetValue(profile, out var entry))
                return false;
            var key = profile + "|" + hand;
            lock (Cache)
            {
                if (!Cache.TryGetValue(key, out d))
                {
                    var s = new Sketch(hand == Hand.Left);
                    entry.Draw(s, hand);
                    d = s.Finish(entry.Name);
                    Cache[key] = d;
                }
            }
            return true;
        }

        // --- The drawings. Everything is written for the right hand with the fingers on the right; the left hand is its mirror. ---

        /// <summary>What differs between the controllers built on the same plan: a ring overhead, a stick, face buttons and a trigger and grip on the finger side.</summary>
        private struct TouchLike
        {
            public float RingX, RingY, RingRx, RingRy, RingRot, RingThick;
            public float BodyWidth;
            public bool MenuOnBoth, Thumbrest, Bumper, Pico;

            public static readonly TouchLike Quest = new TouchLike { RingX = 0.40f, RingY = 0.19f, RingRx = 0.19f, RingRy = 0.10f, RingRot = -22, RingThick = 0.5f, BodyWidth = 1f, Thumbrest = true };
            public static readonly TouchLike ReverbG2 = new TouchLike { RingX = 0.46f, RingY = 0.17f, RingRx = 0.25f, RingRy = 0.075f, RingRot = -8, RingThick = 0.55f, BodyWidth = 0.95f, MenuOnBoth = true };
            public static readonly TouchLike Pico4 = new TouchLike { RingX = 0.36f, RingY = 0.22f, RingRx = 0.22f, RingRy = 0.13f, RingRot = 32, RingThick = 0.55f, BodyWidth = 1.08f, Thumbrest = true, Pico = true };
            public static readonly TouchLike PicoNeo3 = new TouchLike { RingX = 0.46f, RingY = 0.18f, RingRx = 0.21f, RingRy = 0.095f, RingRot = 0, RingThick = 0.62f, BodyWidth = 0.9f, Pico = true };
            public static readonly TouchLike PicoUltra = new TouchLike { RingX = 0.40f, RingY = 0.20f, RingRx = 0.24f, RingRy = 0.085f, RingRot = 14, RingThick = 0.5f, BodyWidth = 1.04f, Pico = true };
            public static readonly TouchLike Cosmos = new TouchLike { RingX = 0.44f, RingY = 0.17f, RingRx = 0.21f, RingRy = 0.12f, RingRot = -12, RingThick = 0.8f, BodyWidth = 1.02f, Bumper = true };
            public static readonly TouchLike Focus3 = new TouchLike { RingX = 0.38f, RingY = 0.21f, RingRx = 0.17f, RingRy = 0.15f, RingRot = 0, RingThick = 0.72f, BodyWidth = 1f, Thumbrest = true };
        }

        /// <summary>The Quest-shaped controllers: a ring above a head with a stick, two face buttons and a Menu button, then the handle.</summary>
        private static void Ringed(Sketch s, Hand hand, TouchLike v)
        {
            var w = v.BodyWidth;
            s.Loop(v.RingX, v.RingY, v.RingRx, v.RingRy, v.RingRot, v.RingThick);
            float Wx(float x) => 0.5f + (x - 0.5f) * w;
            s.Body(new[]
            {
                Wx(0.29f), 0.31f, Wx(0.38f), 0.24f, Wx(0.55f), 0.23f, Wx(0.68f), 0.28f, Wx(0.73f), 0.40f, Wx(0.72f), 0.54f,
                Wx(0.68f), 0.66f, Wx(0.65f), 0.80f, Wx(0.60f), 0.93f, Wx(0.48f), 0.96f, Wx(0.39f), 0.88f, Wx(0.36f), 0.72f,
                Wx(0.33f), 0.58f, Wx(0.27f), 0.44f,
            });
            if (v.Bumper)
                s.Tab(ControllerControl.Bumper, 0.46f, 0.235f, 0.27f, 0.04f);
            s.Stick(0.45f, 0.40f, 0.075f);
            s.Button(ControllerControl.ButtonUpper, 0.62f, 0.38f, 0.04f);
            s.Button(ControllerControl.ButtonLower, 0.60f, 0.52f, 0.04f);
            if (v.MenuOnBoth || hand == Hand.Left)
                s.Pill(ControllerControl.Menu, 0.44f, 0.56f, 0.032f);
            else if (v.Pico || v.Bumper)
                s.Button(ControllerControl.System, 0.44f, 0.56f, 0.028f);
            if (v.Thumbrest)
                s.Hint(ControllerControl.Thumbrest, 0.50f, 0.66f, 0.075f, 0.032f);
            s.Tab(ControllerControl.Trigger, 0.745f, 0.49f, 0.075f, 0.20f);
            s.Tab(ControllerControl.Grip, 0.675f, 0.755f, 0.075f, 0.19f);
        }

        /// <summary>The Index controller: a stick above a trackpad, A and B along the outer edge, a wide handle.</summary>
        private static void Index(Sketch s, Hand hand)
        {
            s.Body(new[]
            {
                0.27f, 0.30f, 0.34f, 0.20f, 0.50f, 0.17f, 0.66f, 0.21f, 0.73f, 0.32f, 0.72f, 0.50f, 0.68f, 0.66f, 0.65f, 0.82f,
                0.60f, 0.94f, 0.46f, 0.96f, 0.38f, 0.86f, 0.35f, 0.68f, 0.30f, 0.52f,
            });
            s.Stick(0.43f, 0.31f, 0.07f);
            s.Pad(0.47f, 0.54f, 0.085f, false);
            s.Zone(ControllerControl.TrackpadForce, 0.47f, 0.54f, 0.04f);
            s.Button(ControllerControl.ButtonUpper, 0.64f, 0.37f, 0.035f);
            s.Button(ControllerControl.ButtonLower, 0.64f, 0.49f, 0.035f);
            s.Tab(ControllerControl.Trigger, 0.745f, 0.40f, 0.075f, 0.20f);
            s.Tab(ControllerControl.Grip, 0.70f, 0.72f, 0.075f, 0.22f);
        }

        /// <summary>The Vive wand: a round head with the trackpad and Menu button, the system button below, a thin handle.</summary>
        private static void ViveWand(Sketch s, Hand hand)
        {
            s.Body(new[]
            {
                0.50f, 0.07f, 0.66f, 0.10f, 0.75f, 0.22f, 0.75f, 0.38f, 0.67f, 0.52f, 0.58f, 0.60f, 0.57f, 0.76f, 0.58f, 0.88f,
                0.54f, 0.96f, 0.46f, 0.96f, 0.42f, 0.88f, 0.43f, 0.76f, 0.42f, 0.60f, 0.33f, 0.52f, 0.25f, 0.38f, 0.25f, 0.22f, 0.34f, 0.10f,
            });
            s.Pad(0.50f, 0.34f, 0.15f, true);
            s.Pill(ControllerControl.Menu, 0.50f, 0.15f, 0.035f);
            s.Button(ControllerControl.System, 0.50f, 0.56f, 0.032f);
            s.Tab(ControllerControl.Trigger, 0.645f, 0.585f, 0.075f, 0.15f);
            s.Tab(ControllerControl.Grip, 0.595f, 0.77f, 0.065f, 0.17f);
        }

        /// <summary>The Windows Mixed Reality controller: a large ring, then Menu, the stick and the trackpad down the face.</summary>
        private static void WindowsMixedReality(Sketch s, Hand hand)
        {
            s.Loop(0.46f, 0.18f, 0.27f, 0.11f, 0, 0.6f);
            s.Body(new[]
            {
                0.30f, 0.30f, 0.38f, 0.23f, 0.56f, 0.22f, 0.68f, 0.27f, 0.72f, 0.40f, 0.71f, 0.55f, 0.67f, 0.66f, 0.64f, 0.80f,
                0.60f, 0.93f, 0.49f, 0.96f, 0.40f, 0.88f, 0.37f, 0.72f, 0.34f, 0.60f, 0.28f, 0.44f,
            });
            s.Pill(ControllerControl.Menu, 0.60f, 0.30f, 0.03f);
            s.Stick(0.48f, 0.40f, 0.065f);
            s.Pad(0.49f, 0.58f, 0.09f, true);
            s.Tab(ControllerControl.Trigger, 0.74f, 0.47f, 0.075f, 0.19f);
            s.Tab(ControllerControl.Grip, 0.675f, 0.775f, 0.075f, 0.17f);
        }

        /// <summary>The Steam Frame controller: bumper over the trigger, a stick, and on the left a D-pad and View, on the right the four face buttons.</summary>
        private static void SteamFrame(Sketch s, Hand hand)
        {
            s.Body(new[]
            {
                0.27f, 0.30f, 0.34f, 0.21f, 0.52f, 0.18f, 0.68f, 0.22f, 0.74f, 0.34f, 0.73f, 0.52f, 0.68f, 0.66f, 0.65f, 0.80f,
                0.60f, 0.93f, 0.48f, 0.96f, 0.39f, 0.88f, 0.36f, 0.72f, 0.31f, 0.56f, 0.26f, 0.44f,
            });
            s.Tab(ControllerControl.Bumper, 0.50f, 0.15f, 0.34f, 0.045f);
            if (hand == Hand.Left)
            {
                s.Dpad(0.47f, 0.34f, 0.19f);
                s.Pill(ControllerControl.View, 0.62f, 0.52f, 0.032f);
                s.Stick(0.45f, 0.58f, 0.07f);
            }
            else
            {
                s.Diamond(0.47f, 0.34f, 0.095f, 0.036f);
                s.Stick(0.45f, 0.58f, 0.07f);
            }
            s.Tab(ControllerControl.Trigger, 0.755f, 0.46f, 0.075f, 0.17f);
            s.Tab(ControllerControl.Grip, 0.69f, 0.755f, 0.075f, 0.19f);
        }

        /// <summary>The simple controller: a slim wand with a Menu button and the select button on the trigger.</summary>
        private static void Simple(Sketch s, Hand hand)
        {
            s.Body(new[]
            {
                0.36f, 0.20f, 0.50f, 0.14f, 0.64f, 0.20f, 0.67f, 0.36f, 0.64f, 0.56f, 0.61f, 0.76f, 0.58f, 0.92f, 0.50f, 0.96f,
                0.42f, 0.92f, 0.39f, 0.76f, 0.36f, 0.56f, 0.33f, 0.36f,
            });
            s.Pill(ControllerControl.Menu, 0.50f, 0.30f, 0.04f);
            s.Tab(ControllerControl.Trigger, 0.69f, 0.46f, 0.07f, 0.20f);
        }

        // --- Sketching ---

        /// <summary>Collects shapes and anchors while a drawing is described, mirroring them for the left hand.</summary>
        private sealed class Sketch
        {
            private readonly bool mirror;
            private readonly ControllerDiagram d = new ControllerDiagram();

            public Sketch(bool mirror) { this.mirror = mirror; }

            public ControllerDiagram Finish(string name) { d.Name = name; return d; }

            private Vector2 V(float x, float y) => new Vector2(mirror ? 1f - x : x, y);

            private ControllerDiagram.Shape Add(Vector2[] ring, ControllerDiagram.ShapeStyle style, ControllerControl? tag, bool fill = true, bool stroke = true, bool closed = true, bool onlyLit = false)
            {
                var s = new ControllerDiagram.Shape { Style = style, Tag = tag, Fill = fill && closed, Stroke = stroke, Closed = closed, HighlightOnly = onlyLit };
                s.Rings.Add(ring);
                d.Shapes.Add(s);
                return s;
            }

            private void Anchor(ControllerControl c, float x, float y, float r) => d.AnchorList.Add((c, V(x, y), r));

            private Vector2[] Ellipse(float cx, float cy, float rx, float ry, float rotDeg = 0, int n = 40)
            {
                var pts = new Vector2[n];
                var rot = rotDeg * (float)Math.PI / 180f;
                float cr = (float)Math.Cos(rot), sr = (float)Math.Sin(rot);
                for (int i = 0; i < n; i++)
                {
                    var a = i * 2 * (float)Math.PI / n;
                    float ex = rx * (float)Math.Cos(a), ey = ry * (float)Math.Sin(a);
                    pts[i] = V(cx + ex * cr - ey * sr, cy + ex * sr + ey * cr);
                }
                return pts;
            }

            private Vector2[] RoundRect(float cx, float cy, float w, float h, float r)
            {
                r = Math.Min(r, Math.Min(w, h) / 2);
                var pts = new List<Vector2>();
                float[] cxs = { cx + w / 2 - r, cx + w / 2 - r, cx - w / 2 + r, cx - w / 2 + r };
                float[] cys = { cy - h / 2 + r, cy + h / 2 - r, cy + h / 2 - r, cy - h / 2 + r };
                for (int corner = 0; corner < 4; corner++)
                    for (int i = 0; i <= 6; i++)
                    {
                        var a = (-90 + corner * 90 + i * 15) * (float)Math.PI / 180f;
                        pts.Add(V(cxs[corner] + r * (float)Math.Cos(a), cys[corner] + r * (float)Math.Sin(a)));
                    }
                return pts.ToArray();
            }

            /// <summary>The controller's outline from a closed list of x, y pairs, smoothed through each point.</summary>
            public void Body(float[] xy)
            {
                var n = xy.Length / 2;
                var pts = new List<Vector2>();
                for (int i = 0; i < n; i++)
                {
                    Vector2 p0 = new Vector2(xy[((i + n - 1) % n) * 2], xy[((i + n - 1) % n) * 2 + 1]), p1 = new Vector2(xy[i * 2], xy[i * 2 + 1]);
                    Vector2 p2 = new Vector2(xy[((i + 1) % n) * 2], xy[((i + 1) % n) * 2 + 1]), p3 = new Vector2(xy[((i + 2) % n) * 2], xy[((i + 2) % n) * 2 + 1]);
                    for (int k = 0; k < 8; k++)
                    {
                        float t = k / 8f, t2 = t * t, t3 = t2 * t;
                        float x = 0.5f * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                        float y = 0.5f * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                        pts.Add(V(x, y));
                    }
                }
                Add(pts.ToArray(), ControllerDiagram.ShapeStyle.Body, null);
            }

            /// <summary>A tracking ring: a tilted band with a hole.</summary>
            public void Loop(float cx, float cy, float rx, float ry, float rotDeg, float thick)
            {
                var s = Add(Ellipse(cx, cy, rx, ry, rotDeg, 48), ControllerDiagram.ShapeStyle.Body, null);
                s.Rings.Add(Ellipse(cx, cy, rx * thick, ry * thick, rotDeg, 40));
            }

            /// <summary>A faint ellipse that is not a button: a thumbrest, or the top of an Index controller's tracking loop.</summary>
            public void Hint(ControllerControl? c, float x, float y, float rx, float ry)
            {
                Add(Ellipse(x, y, rx, ry), ControllerDiagram.ShapeStyle.Faint, c, fill: false);
                if (c.HasValue)
                    Anchor(c.Value, x, y, Math.Max(rx, ry));
            }

            public void Stick(float x, float y, float r)
            {
                Add(Ellipse(x, y, r, r), ControllerDiagram.ShapeStyle.Part, ControllerControl.Thumbstick);
                Add(Ellipse(x, y, r * 0.55f, r * 0.55f, 0, 28), ControllerDiagram.ShapeStyle.Part, ControllerControl.ThumbstickClick);
                Anchor(ControllerControl.Thumbstick, x, y, r);
                Anchor(ControllerControl.ThumbstickClick, x, y, r * 0.55f);
            }

            public void Button(ControllerControl c, float x, float y, float r)
            {
                Add(Ellipse(x, y, r, r, 0, 28), ControllerDiagram.ShapeStyle.Part, c);
                Anchor(c, x, y, r);
            }

            public void Pill(ControllerControl c, float x, float y, float r)
            {
                Add(RoundRect(x, y, r * 3.2f, r * 1.7f, r * 0.85f), ControllerDiagram.ShapeStyle.Part, c);
                Anchor(c, x, y, r * 1.6f);
            }

            /// <summary>A tab on the finger side or top edge (trigger, grip, bumper), given by its centre and size.</summary>
            public void Tab(ControllerControl c, float cx, float cy, float w, float h)
            {
                Add(RoundRect(cx, cy, w, h, Math.Min(w, h) * 0.45f), ControllerDiagram.ShapeStyle.Part, c);
                Anchor(c, cx, cy, Math.Max(w, h) * 0.5f);
            }

            /// <summary>A trackpad, with the zones (edges and middle) that a click can fall in when asked for.</summary>
            public void Pad(float x, float y, float r, bool zones)
            {
                Add(Ellipse(x, y, r, r, 0, 48), ControllerDiagram.ShapeStyle.Part, ControllerControl.Trackpad);
                Anchor(ControllerControl.Trackpad, x, y, r);
                if (!zones)
                    return;
                Add(new[] { V(x - r * 0.85f, y), V(x + r * 0.85f, y) }, ControllerDiagram.ShapeStyle.Faint, null, closed: false);
                Add(new[] { V(x, y - r * 0.85f), V(x, y + r * 0.85f) }, ControllerDiagram.ShapeStyle.Faint, null, closed: false);
                Zone(ControllerControl.TrackpadUp, x, y - r * 0.62f, r * 0.26f);
                Zone(ControllerControl.TrackpadDown, x, y + r * 0.62f, r * 0.26f);
                Zone(ControllerControl.TrackpadLeft, x - r * 0.62f, y, r * 0.26f);
                Zone(ControllerControl.TrackpadRight, x + r * 0.62f, y, r * 0.26f);
                Zone(ControllerControl.TrackpadCentre, x, y, r * 0.3f);
            }

            public void Zone(ControllerControl c, float x, float y, float r)
            {
                Add(Ellipse(x, y, r, r, 0, 24), ControllerDiagram.ShapeStyle.Part, c, onlyLit: true);
                Anchor(c, x, y, r);
            }

            /// <summary>A D-pad: four arms of a cross, each its own control.</summary>
            public void Dpad(float x, float y, float size)
            {
                var arm = size * 0.34f;
                var reach = size * 0.34f;
                Arm(ControllerControl.DpadUp, x, y - reach, arm, arm * 1.05f);
                Arm(ControllerControl.DpadDown, x, y + reach, arm, arm * 1.05f);
                Arm(ControllerControl.DpadLeft, x - reach, y, arm * 1.05f, arm);
                Arm(ControllerControl.DpadRight, x + reach, y, arm * 1.05f, arm);
            }

            private void Arm(ControllerControl c, float x, float y, float w, float h)
            {
                Add(RoundRect(x, y, w, h, Math.Min(w, h) * 0.3f), ControllerDiagram.ShapeStyle.Part, c);
                Anchor(c, x, y, Math.Min(w, h) * 0.55f);
            }

            /// <summary>Four face buttons in a diamond. Only the bottom and right ones are controls the plugin names (the lower and upper face buttons); the others are drawn faint.</summary>
            public void Diamond(float x, float y, float reach, float r)
            {
                Button(ControllerControl.ButtonLower, x, y + reach, r);
                Button(ControllerControl.ButtonUpper, x + reach, y, r);
                Add(Ellipse(x - reach, y, r, r, 0, 28), ControllerDiagram.ShapeStyle.Faint, null, fill: false);
                Add(Ellipse(x, y - reach, r, r, 0, 28), ControllerDiagram.ShapeStyle.Faint, null, fill: false);
            }
        }

        // --- Rendering ---

        private const int Supersample = 4;

        /// <summary>One filled or stroked layer in pixel space: polygons (x, y pairs, closed) and the premultiplied colour they are drawn in.</summary>
        private sealed class Layer
        {
            public readonly List<float[]> Polys = new List<float[]>();
            public readonly List<float> MinY = new List<float>(), MaxY = new List<float>();
            public bool EvenOdd;
            public float R, G, B, A;

            public void Add(float[] poly)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 1; i < poly.Length; i += 2)
                {
                    lo = Math.Min(lo, poly[i]);
                    hi = Math.Max(hi, poly[i]);
                }
                Polys.Add(poly);
                MinY.Add(lo);
                MaxY.Add(hi);
            }

            public void SetColour(float r, float g, float b, float a)
            {
                A = a;
                R = r * a;
                G = g * a;
                B = b * a;
            }
        }

        /// <summary>
        /// Draws a controller as RGBA8 (width x height x 4 bytes, row 0 on top) on a transparent background: pale blue-white lines
        /// over a dark fill, and the highlighted control, if given, in the game's selection yellow. The drawing is square and
        /// centred in the image. Edges are anti-aliased by supersampling.
        /// </summary>
        public static byte[] Render(ControllerDiagram d, int width, int height, ControllerControl? highlight)
        {
            var layers = new List<Layer>();
            float scale = Math.Min(width, height) * Supersample;
            float ox = (width * Supersample - scale) / 2, oy = (height * Supersample - scale) / 2;
            bool anyLit = false;

            foreach (var shape in d.Shapes)
            {
                bool lit = highlight.HasValue && shape.Tag == highlight;
                if (shape.HighlightOnly && !lit)
                    continue;
                anyLit |= lit;
                float lineW = (shape.Style == ControllerDiagram.ShapeStyle.Body ? 0.012f : shape.Style == ControllerDiagram.ShapeStyle.Part ? 0.008f : 0.0055f) * scale;
                if (lit)
                    lineW *= 1.25f;
                if (shape.Fill)
                {
                    var fill = new Layer { EvenOdd = true };
                    foreach (var ring in shape.Rings)
                        fill.Add(ToPixels(ring, scale, ox, oy));
                    if (lit)
                        fill.SetColour(1f, 0.784f, 0.196f, 0.42f);
                    else if (shape.Style == ControllerDiagram.ShapeStyle.Body)
                        fill.SetColour(0.04f, 0.07f, 0.11f, 0.70f);
                    else
                        fill.SetColour(0.10f, 0.16f, 0.22f, 0.94f);
                    layers.Add(fill);
                }
                if (shape.Stroke)
                {
                    var line = new Layer();
                    foreach (var ring in shape.Rings)
                        AddStroke(line, ToPixels(ring, scale, ox, oy), shape.Closed, lineW / 2);
                    if (lit)
                        line.SetColour(1f, 0.784f, 0.196f, 1f);
                    else
                        line.SetColour(0.843f, 0.906f, 0.949f, shape.Style == ControllerDiagram.ShapeStyle.Faint ? 0.55f : 1f);
                    layers.Add(line);
                }
            }

            if (highlight.HasValue && !anyLit)
            {
                foreach (var a in d.Anchors)
                    if (a.Control == highlight.Value)
                    {
                        var ring = new Layer();
                        var pts = new float[48 * 2];
                        for (int i = 0; i < 48; i++)
                        {
                            var ang = i * 2 * (float)Math.PI / 48;
                            pts[i * 2] = ox + (a.At.X + (float)Math.Cos(ang) * a.Radius * 1.4f) * scale;
                            pts[i * 2 + 1] = oy + (a.At.Y + (float)Math.Sin(ang) * a.Radius * 1.4f) * scale;
                        }
                        AddStroke(ring, pts, true, 0.0045f * scale);
                        ring.SetColour(1f, 0.784f, 0.196f, 1f);
                        layers.Add(ring);
                    }
            }

            var result = new byte[width * height * 4];
            int sw = width * Supersample;
            var rr = new float[sw]; var gg = new float[sw]; var bb = new float[sw]; var aa = new float[sw];
            var mask = new bool[sw];
            var xs = new List<float>();
            var acc = new float[width * 4];
            float norm = 1f / (Supersample * Supersample);

            for (int y = 0; y < height; y++)
            {
                Array.Clear(acc, 0, acc.Length);
                for (int sub = 0; sub < Supersample; sub++)
                {
                    float sy = y * Supersample + sub + 0.5f;
                    Array.Clear(rr, 0, sw); Array.Clear(gg, 0, sw); Array.Clear(bb, 0, sw); Array.Clear(aa, 0, sw);
                    foreach (var layer in layers)
                    {
                        int lo = sw, hi = -1;
                        if (layer.EvenOdd)
                        {
                            xs.Clear();
                            for (int p = 0; p < layer.Polys.Count; p++)
                                if (sy >= layer.MinY[p] && sy < layer.MaxY[p])
                                    Crossings(layer.Polys[p], sy, xs);
                            Spans(xs, mask, sw, ref lo, ref hi);
                        }
                        else
                        {
                            for (int p = 0; p < layer.Polys.Count; p++)
                            {
                                if (sy < layer.MinY[p] || sy >= layer.MaxY[p])
                                    continue;
                                xs.Clear();
                                Crossings(layer.Polys[p], sy, xs);
                                Spans(xs, mask, sw, ref lo, ref hi);
                            }
                        }
                        float inv = 1f - layer.A;
                        for (int x = lo; x <= hi; x++)
                        {
                            if (!mask[x])
                                continue;
                            mask[x] = false;
                            rr[x] = layer.R + rr[x] * inv;
                            gg[x] = layer.G + gg[x] * inv;
                            bb[x] = layer.B + bb[x] * inv;
                            aa[x] = layer.A + aa[x] * inv;
                        }
                    }
                    for (int x = 0; x < sw; x++)
                    {
                        int px = (x / Supersample) * 4;
                        acc[px] += rr[x]; acc[px + 1] += gg[x]; acc[px + 2] += bb[x]; acc[px + 3] += aa[x];
                    }
                }
                for (int x = 0; x < width; x++)
                {
                    float a = acc[x * 4 + 3] * norm;
                    int o = (y * width + x) * 4;
                    if (a <= 0.0001f)
                        continue;
                    result[o] = ToByte(acc[x * 4] * norm / a);
                    result[o + 1] = ToByte(acc[x * 4 + 1] * norm / a);
                    result[o + 2] = ToByte(acc[x * 4 + 2] * norm / a);
                    result[o + 3] = ToByte(a);
                }
            }
            return result;
        }

        private static byte ToByte(float v) => (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));

        private static float[] ToPixels(Vector2[] ring, float scale, float ox, float oy)
        {
            var pts = new float[ring.Length * 2];
            for (int i = 0; i < ring.Length; i++)
            {
                pts[i * 2] = ox + ring[i].X * scale;
                pts[i * 2 + 1] = oy + ring[i].Y * scale;
            }
            return pts;
        }

        /// <summary>Adds a stroke along a path as a union of one quad per segment and a round cap at each point.</summary>
        private static void AddStroke(Layer layer, float[] pts, bool closed, float half)
        {
            int n = pts.Length / 2;
            int segs = closed ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                float x0 = pts[i * 2], y0 = pts[i * 2 + 1];
                int j = (i + 1) % n;
                float x1 = pts[j * 2], y1 = pts[j * 2 + 1];
                float dx = x1 - x0, dy = y1 - y0;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-4f)
                    continue;
                float nx = -dy / len * half, ny = dx / len * half;
                layer.Add(new[] { x0 + nx, y0 + ny, x1 + nx, y1 + ny, x1 - nx, y1 - ny, x0 - nx, y0 - ny });
            }
            for (int i = 0; i < n; i++)
            {
                var cap = new float[16];
                for (int k = 0; k < 8; k++)
                {
                    var a = k * (float)Math.PI / 4 + 0.3927f;
                    cap[k * 2] = pts[i * 2] + (float)Math.Cos(a) * half * 1.082f;
                    cap[k * 2 + 1] = pts[i * 2 + 1] + (float)Math.Sin(a) * half * 1.082f;
                }
                layer.Add(cap);
            }
        }

        private static void Crossings(float[] poly, float y, List<float> xs)
        {
            int n = poly.Length / 2;
            float px = poly[(n - 1) * 2], py = poly[(n - 1) * 2 + 1];
            for (int i = 0; i < n; i++)
            {
                float cx = poly[i * 2], cy = poly[i * 2 + 1];
                if ((py <= y) != (cy <= y))
                    xs.Add(px + (y - py) / (cy - py) * (cx - px));
                px = cx;
                py = cy;
            }
        }

        private static void Spans(List<float> xs, bool[] mask, int sw, ref int lo, ref int hi)
        {
            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                int a = Math.Max(0, (int)Math.Ceiling(xs[i] - 0.5f));
                int b = Math.Min(sw - 1, (int)Math.Ceiling(xs[i + 1] - 0.5f) - 1);
                if (a > b)
                    continue;
                for (int x = a; x <= b; x++)
                    mask[x] = true;
                if (a < lo) lo = a;
                if (b > hi) hi = b;
            }
        }
    }
}
