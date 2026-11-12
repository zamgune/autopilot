// UI Toolkit pieces of the Autopilot window (The Flatness: Aura's flatness-dark look): the colours, the controller
// icon, the buttons' glyphs, the spinner, the tick, stacked probability bars and the stage track, drawn with Painter2D;
// and small element builders.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Autopilot.Playtest.Editor
{
    public static class AutopilotUI
    {
        public const string Root = "Packages/com.ramenvr.autopilot/Editor/UI/";

        // The Flatness's tokens: the window's USS has the same colours
        public static readonly Color Ground = Hex("#121316");
        /// <summary>The history rail, the policy log's side panel.</summary>
        public static readonly Color Panel = Hex("#0f1013");
        /// <summary>A plate: cards, the feed, findings.</summary>
        public static readonly Color Raised = Hex("#181a1e");
        public static readonly Color Popover = Hex("#15171b");
        public static readonly Color Hover = Hex("#1d2026");
        public static readonly Color RowActive = Hex("#2a1b10");
        /// <summary>A well: the prompt, inputs, the video, evidence.</summary>
        public static readonly Color Well = Hex("#0d0e11");
        /// <summary>The divider rule (#24262b): under headers, around plates, above footers.</summary>
        public static readonly Color Line = Hex("#24262b");
        /// <summary>The control outline (#2c2f36): buttons, inputs, the composer.</summary>
        public static readonly Color Line2 = Hex("#2c2f36");
        public static readonly Color Text = Hex("#e6e6e8");
        /// <summary>Reading text: reasons, prompts.</summary>
        public static readonly Color Text2 = Hex("#d3d5da");
        /// <summary>Row text, feed lines.</summary>
        public static readonly Color RowText = Hex("#a4a8b0");
        public static readonly Color Muted = Hex("#8b8f98");
        /// <summary>(the old dim grey is retired: muted)</summary>
        public static readonly Color Dim = Muted;
        /// <summary>The interactive colour: the main buttons, the active tab, progress, what is live or picked.</summary>
        public static readonly Color Accent = Hex("#f0873a");
        public static readonly Color AccentHover = Hex("#f59b57");
        /// <summary>Ink on any fill (the main buttons, the verdict).</summary>
        public static readonly Color Ink = Hex("#141517");
        public static readonly Color Focus = Hex("#ffb066");
        /// <summary>Good: Confirmed, Goal hit, ready, connected.</summary>
        public static readonly Color Success = Hex("#74b078");
        public static readonly Color SuccessFill = Hex("#16281a");
        public static readonly Color Red = Hex("#e26a62");
        public static readonly Color Amber = Hex("#e8a94e");
        public static readonly Color Info = Hex("#7aa5d8");
        public static readonly Color InfoLine = Hex("#3a5578");
        public static readonly Color Violet = Hex("#a08ad8");
        public static readonly Color Teal = Hex("#5fb3b3");
        public static readonly Color Neutral = Hex("#c9ccd2");

        /// <summary>The actions' colours, by order of first appearance in a run.</summary>
        public static readonly Color[] Palette =
        {
            Accent, Info, Amber, Success, Violet, Red, Teal, Neutral,
        };

        public static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        public static Color ActionColor(int index)
        {
            return index < 0 ? Muted : Palette[index % Palette.Length];
        }

        /// <summary>The fonts the window's USS draws with (Editor/UI/Fonts, SIL OFL).</summary>
        public static readonly string[] Fonts =
        {
            "IBMPlexSans-Regular.ttf", "IBMPlexSans-Medium.ttf", "IBMPlexSans-SemiBold.ttf", "IBMPlexMono-Regular.ttf", "IBMPlexMono-Medium.ttf",
        };

        /// <summary>Whether every bundled font can be read (under <paramref name="root"/>, the package's UI folder by
        /// default). One that can't is drawn in the editor's own font; the window says so once it opens.</summary>
        public static bool FontsFound(string root = Root)
        {
            foreach (var name in Fonts)
            {
                Font font = null;
                try
                {
                    font = AssetDatabase.LoadAssetAtPath<Font>(root + "Fonts/" + name);
                }
                catch (Exception)
                {
                    // (unreadable: not found)
                }
                if (font == null) return false;
            }
            return true;
        }

        static StyleSheet sheet;

        public static StyleSheet Sheet
        {
            get
            {
                if (sheet == null) sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(Root + "Autopilot.uss");
                return sheet;
            }
        }

        // -- builders ----------------------------------------------------------------------------------------

        public static Label L(string text, string classes = null)
        {
            var l = new Label(text ?? "");
            Classes(l, classes);
            return l;
        }

        public static VisualElement Box(string classes = null)
        {
            var e = new VisualElement();
            Classes(e, classes);
            return e;
        }

        public static VisualElement Row(string classes = null)
        {
            return Box("row " + (classes ?? ""));
        }

        public static VisualElement Spacer()
        {
            return Box("grow");
        }

        public static void Classes(VisualElement e, string classes)
        {
            if (string.IsNullOrEmpty(classes)) return;
            foreach (var c in classes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) e.AddToClassList(c);
        }

        /// <summary>A button (square, outlined; classes primary | quiet | danger | danger-fill | small | on): a
        /// VisualElement with its words in sentence case.</summary>
        public static VisualElement Btn(string text, Action click, string classes = null)
        {
            var b = Box("btn " + (classes ?? ""));
            b.Add(L(text));
            b.AddManipulator(new Clickable(() => click?.Invoke()));
            b.focusable = true;
            b.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Space)
                {
                    click?.Invoke();
                    e.StopPropagation();
                }
            });
            return b;
        }

        /// <summary>A square button showing a glyph (no words: <paramref name="name"/> is its tooltip and what it is called).</summary>
        public static VisualElement IconBtn(Glyph.Shape shape, string name, Action click, string classes = null)
        {
            var b = Box("btn icon " + (classes ?? ""));
            b.Add(new Glyph(shape, shape == Glyph.Shape.Play || shape == Glyph.Shape.Pause || shape == Glyph.Shape.Prev || shape == Glyph.Shape.Next ? 12 : 14));
            b.tooltip = name;
            b.name = name;
            b.AddManipulator(new Clickable(() => click?.Invoke()));
            b.focusable = true;
            b.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Space)
                {
                    click?.Invoke();
                    e.StopPropagation();
                }
            });
            return b;
        }

        /// <summary>A tag: square, tinted, mono in capitals. tone: lime | red | amber | info | muted | accent.</summary>
        public static VisualElement Tag(string text, string tone)
        {
            var t = Box("tag " + tone);
            t.Add(L((text ?? "").ToUpperInvariant()));
            return t;
        }

        /// <summary>A plate (cards, the feed, findings): #181a1e, a #24262b rule round it, radius 8.</summary>
        public static VisualElement Card(string classes = "plate")
        {
            return Box(classes);
        }

        /// <summary>A micro-label: mono, capitals; "π(a | s)" keeps its case.</summary>
        public static Label Lbl(string text, string classes = null)
        {
            var upper = (text ?? "").ToUpperInvariant().Replace("Π(A | S)", "π(a | s)");
            return L(upper, "lbl " + (classes ?? ""));
        }

        public static Label Chip(string text, bool on, Action click)
        {
            var c = L(text, "chip" + (on ? " on" : ""));
            c.AddManipulator(new Clickable(() => click?.Invoke()));
            return c;
        }

        /// <summary>Mono capitals after a round dot of their colour ("● CONNECTED", "● LIVE": the fonts have no ●, so
        /// the dot is drawn).</summary>
        public static VisualElement DotLabel(string text, Color color, string classes = null)
        {
            var row = Box("dotlabel " + (classes ?? ""));
            row.Add(RoundDot(color));
            var l = L(text, null);
            l.style.color = color;
            row.Add(l);
            return row;
        }

        /// <summary>A round 6 px dot.</summary>
        public static VisualElement RoundDot(Color color)
        {
            var d = Box("round-dot");
            d.style.backgroundColor = color;
            return d;
        }

        public static TextField Field(string value, bool multiline, Action<string> changed, string placeholder = null,
                                      ScrollerVisibility verticalScroller = ScrollerVisibility.Auto)
        {
#if UNITY_6000_0_OR_NEWER
            var f = new TextField { value = value ?? "", multiline = multiline };
            if (multiline) f.verticalScrollerVisibility = verticalScroller;
            if (!string.IsNullOrEmpty(placeholder))
            {
                f.textEdition.placeholder = placeholder;
                f.textEdition.hidePlaceholderOnFocus = true;
            }
#else
            var f = new HintedTextField { value = value ?? "", multiline = multiline };
            if (multiline) f.SetVerticalScrollerVisibility(verticalScroller);
            if (!string.IsNullOrEmpty(placeholder)) f.AddHint(placeholder);
#endif
            if (multiline) f.AddToClassList("multiline");
            f.RegisterValueChangedCallback(e => changed?.Invoke(e.newValue));
            return f;
        }

#if !UNITY_6000_0_OR_NEWER
        // Unity 2022 has no native placeholder. Keep the hint out of the editable value, including on password fields.
        sealed class HintedTextField : TextField
        {
            TextElement hint;
            bool editing;

            public void AddHint(string text)
            {
                var input = this.Q(className: TextField.inputUssClassName);
                hint = new TextElement { text = text, name = "autopilot-placeholder", pickingMode = PickingMode.Ignore, enableRichText = false };
                hint.style.position = Position.Absolute;
                hint.style.color = Muted;
                input.hierarchy.Add(hint);
                input.RegisterCallback<GeometryChangedEvent>(_ =>
                {
                    var style = input.resolvedStyle;
                    hint.style.left = style.paddingLeft;
                    hint.style.right = style.paddingRight;
                    hint.style.top = style.paddingTop;
                    hint.style.bottom = style.paddingBottom;
                });
                RegisterCallback<FocusInEvent>(_ => SetEditing(true));
                RegisterCallback<FocusOutEvent>(e => SetEditing(e.relatedTarget is VisualElement next && Contains(next)));
                RegisterCallback<DetachFromPanelEvent>(_ => SetEditing(false));
                RefreshHint();
            }

            void SetEditing(bool focused)
            {
                editing = focused;
                RefreshHint();
            }

            void RefreshHint()
            {
                if (hint != null) hint.style.display = !editing && string.IsNullOrEmpty(value) ? DisplayStyle.Flex : DisplayStyle.None;
            }

            public override void SetValueWithoutNotify(string newValue)
            {
                base.SetValueWithoutNotify(newValue);
                RefreshHint();
            }
        }
#endif

        /// <summary>A whole number in a field (mono, right-aligned): Minutes, Time limit.</summary>
        public static IntegerField NumField(int value, string classes = null)
        {
            var f = new IntegerField { value = value };
            f.AddToClassList("num-field");
            Classes(f, classes);
            return f;
        }

        /// <summary>A switch (Aura's pill: off #2c2f36, on #f0873a, a white knob) with its words after it.</summary>
        public static Toggle Switch(string text, bool value)
        {
            var t = new Toggle { text = text, value = value };
            t.AddToClassList("switch");
            var mark = t.Q(className: "unity-toggle__checkmark");
            if (mark != null)
            {
                var knob = Box("knob");
                knob.pickingMode = PickingMode.Ignore;
                mark.Add(knob);
            }
            return t;
        }

        // -- the icon ----------------------------------------------------------------------------------------

        // A game controller in a 24 x 24 box (the same shape as the Unreal and Godot icons' Autopilot.svg): the body's
        // outline, the d-pad's two strokes and two face buttons
        static readonly Vector2[] Body =
        {
            new Vector2(7, 7), new Vector2(17, 7), new Vector2(19.5f, 7.6f), new Vector2(21.2f, 9.2f), new Vector2(22, 11.5f),
            new Vector2(22.8f, 15.5f), new Vector2(22.4f, 17.2f), new Vector2(21, 18), new Vector2(19.4f, 17.8f),
            new Vector2(18, 16.6f), new Vector2(16.6f, 15.5f), new Vector2(7.4f, 15.5f), new Vector2(6, 16.6f),
            new Vector2(4.6f, 17.8f), new Vector2(3, 18), new Vector2(1.6f, 17.2f), new Vector2(1.2f, 15.5f),
            new Vector2(2, 11.5f), new Vector2(2.8f, 9.2f), new Vector2(4.5f, 7.6f),
        };
        static readonly Vector2[,] Pad = { { new Vector2(7, 9.6f), new Vector2(7, 13.4f) }, { new Vector2(5.1f, 11.5f), new Vector2(8.9f, 11.5f) } };
        static readonly Vector2[] Buttons = { new Vector2(16, 10.4f), new Vector2(18.2f, 12.6f) };
        const float ButtonRadius = 1.15f;
        const float Stroke = 1.9f;

        /// <summary>The controller (24 x 24 box) drawn at size px.</summary>
        public sealed class Logo : VisualElement
        {
            readonly Color color;

            public Logo(float size, Color? color = null)
            {
                this.color = color ?? Accent;
                style.width = size;
                style.height = size;
                style.flexShrink = 0;
                generateVisualContent += Draw;
            }

            void Draw(MeshGenerationContext mgc)
            {
                var s = contentRect.width / 24f;
                var p = mgc.painter2D;
                p.strokeColor = color;
                p.lineWidth = Stroke * s;
                p.lineCap = LineCap.Round;
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                p.MoveTo(Body[0] * s);
                for (var i = 1; i < Body.Length; ++i)
                    p.LineTo(Body[i] * s);
                p.ClosePath();
                p.Stroke();
                for (var i = 0; i < 2; ++i)
                {
                    p.BeginPath();
                    p.MoveTo(Pad[i, 0] * s);
                    p.LineTo(Pad[i, 1] * s);
                    p.Stroke();
                }
                p.fillColor = color;
                foreach (var b in Buttons)
                {
                    p.BeginPath();
                    p.Arc(b * s, ButtonRadius * s, Angle.Degrees(0), Angle.Degrees(360));
                    p.ClosePath();
                    p.Fill();
                }
            }
        }

        /// <summary>A button's glyph in a 24 x 24 box (the design's icons), drawn in the USS's --glyph colour (the
        /// button's words' colour: it follows hover and "on"): the gear (Settings), play, pause, a circular arrow (Run
        /// again), a clipboard (Copy report), a folder (the run's folder, the recording in the file manager) and a
        /// chevron (the narrow dock's history list).</summary>
        public sealed class Glyph : VisualElement
        {
            public enum Shape { Gear, Play, Pause, Retry, Clipboard, Folder, Chevron, Prev, Next, Path }

            /// <summary>The activity feed's icons as Lucide's paths (a 24 x 24 box, stroked 1.9), by the names
            /// AutopilotText.ActivityIcon gives: bot, search, binoculars, pencil, flask-conical, gamepad-2, eye, rotate-ccw,
            /// message-square, flag, megaphone, notebook-pen. Drawn where the editor has no icon of the meaning.</summary>
            public static readonly Dictionary<string, string[]> Paths = new Dictionary<string, string[]>
            {
                ["robot"] = new[] { "M12 8V4H8", "M6 8h12a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2z", "M2 14h2", "M20 14h2", "M15 13v2", "M9 13v2" },
                ["magnifier"] = new[] { "M19 11a8 8 0 1 1-16 0a8 8 0 1 1 16 0z", "m21 21-4.3-4.3" },
                ["binoculars"] = new[] { "M10 10h4", "M19 7V4a1 1 0 0 0-1-1h-2a1 1 0 0 0-1 1v3", "M20 21a2 2 0 0 0 2-2v-3.851c0-1.39-2-2.962-2-4.829V8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v11a2 2 0 0 0 2 2z", "M22 16L2 16", "M4 21a2 2 0 0 1-2-2v-3.851c0-1.39 2-2.962 2-4.829V8a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v11a2 2 0 0 1-2 2z", "M9 7V4a1 1 0 0 0-1-1H6a1 1 0 0 0-1 1v3" },
                ["pencil"] = new[] { "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z", "m15 5 4 4" },
                ["flask"] = new[] { "M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2", "M6.453 15h11.094", "M8.5 2h7" },
                ["gamepad"] = new[] { "M6 11h4", "M8 9v4", "M15 12h.01", "M18 10h.01", "M17.32 5H6.68a4 4 0 0 0-3.978 3.59c-.006.052-.01.101-.017.152C2.604 9.416 2 14.456 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.414-1.414A2 2 0 0 1 9.828 16h4.344a2 2 0 0 1 1.414.586L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.545-.604-6.584-.685-7.258-.007-.05-.011-.1-.017-.151A4 4 0 0 0 17.32 5z" },
                ["eye"] = new[] { "M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0", "M15 12a3 3 0 1 1-6 0a3 3 0 1 1 6 0z" },
                ["circular-arrow"] = new[] { "M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8", "M3 3v5h5" },
                ["speech-bubble"] = new[] { "M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" },
                ["flag"] = new[] { "M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z", "M4 22v-7" },
                ["megaphone"] = new[] { "m3 11 18-5v12L3 14v-3z", "M11.6 16.8a3 3 0 1 1-5.8-1.6" },
                ["notebook"] = new[] { "M13.4 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-7.4", "M2 6h4", "M2 10h4", "M2 14h4", "M2 18h4", "M21.378 5.626a1 1 0 1 0-3.004-3.004l-5.01 5.012a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506z" },
            };

            static readonly CustomStyleProperty<Color> ColorProp = new CustomStyleProperty<Color>("--glyph");
            Color color = Text;
            Shape shape;
            string[] paths;  // (Shape.Path: one of Paths)

            /// <summary>One of Paths (the activity feed's icons) at size, stroked in --glyph.</summary>
            public Glyph(string icon, float size = 12) : this(Shape.Path, size)
            {
                paths = Paths[icon];
            }

            public Glyph(Shape shape, float size = 14)
            {
                this.shape = shape;
                AddToClassList("glyph");
                pickingMode = PickingMode.Ignore;
                style.width = size;
                style.height = size;
                style.flexShrink = 0;
                RegisterCallback<CustomStyleResolvedEvent>(e =>
                {
                    color = e.customStyle.TryGetValue(ColorProp, out var c) ? c : Text;
                    MarkDirtyRepaint();
                });
                generateVisualContent += Draw;
            }

            public Shape Is
            {
                get => shape;
                set
                {
                    if (shape == value) return;
                    shape = value;
                    MarkDirtyRepaint();
                }
            }

            void Draw(MeshGenerationContext mgc)
            {
                var s = contentRect.width / 24f;
                if (s <= 0) return;
                var p = mgc.painter2D;
                p.strokeColor = color;
                p.fillColor = color;
                p.lineWidth = 1.9f * s;
                p.lineCap = LineCap.Round;
                p.lineJoin = LineJoin.Round;
                switch (shape)
                {
                    case Shape.Gear:
                    {
                        // the design's gear (Lucide's settings icon): its outline from GearPath, and the hub (r 3)
                        p.lineWidth = 1.8f * s;
                        var outline = GearOutline;
                        p.BeginPath();
                        p.MoveTo(outline[0] * s);
                        for (var i = 1; i < outline.Count; ++i) p.LineTo(outline[i] * s);
                        p.ClosePath();
                        p.Stroke();
                        p.BeginPath();
                        p.Arc(new Vector2(12, 12) * s, 3f * s, Angle.Degrees(0), Angle.Degrees(360));
                        p.Stroke();
                        break;
                    }
                    case Shape.Play:
                        // M6 4v16l14-8z
                        p.BeginPath();
                        p.MoveTo(new Vector2(6, 4) * s);
                        p.LineTo(new Vector2(20, 12) * s);
                        p.LineTo(new Vector2(6, 20) * s);
                        p.ClosePath();
                        p.Fill();
                        break;
                    case Shape.Pause:
                        FillRect(p, 5 * s, 4 * s, 5 * s, 16 * s, color);
                        FillRect(p, 14 * s, 4 * s, 5 * s, 16 * s, color);
                        break;
                    case Shape.Retry:
                    {
                        // M3 12a9 9 0 1 0 3-6.7 (a circle open at the top left), M3 4v5h5 (the arrowhead)
                        p.lineWidth = 2f * s;
                        var c = new Vector2(12, 12) * s;
                        p.BeginPath();
                        p.MoveTo(new Vector2(3, 12) * s);
                        p.Arc(c, 9 * s, Angle.Degrees(180), Angle.Degrees(228), ArcDirection.CounterClockwise);
                        p.Stroke();
                        p.BeginPath();
                        p.MoveTo(new Vector2(3, 4) * s);
                        p.LineTo(new Vector2(3, 9) * s);
                        p.LineTo(new Vector2(8, 9) * s);
                        p.Stroke();
                        break;
                    }
                    case Shape.Clipboard:
                        // rect 8,3 8x4 (the clip), M16 5h3v16H5V5h3 (the board)
                        p.BeginPath();
                        p.MoveTo(new Vector2(8, 3) * s);
                        p.LineTo(new Vector2(16, 3) * s);
                        p.LineTo(new Vector2(16, 7) * s);
                        p.LineTo(new Vector2(8, 7) * s);
                        p.ClosePath();
                        p.Stroke();
                        p.BeginPath();
                        p.MoveTo(new Vector2(16, 5) * s);
                        p.LineTo(new Vector2(19, 5) * s);
                        p.LineTo(new Vector2(19, 21) * s);
                        p.LineTo(new Vector2(5, 21) * s);
                        p.LineTo(new Vector2(5, 5) * s);
                        p.LineTo(new Vector2(8, 5) * s);
                        p.Stroke();
                        break;
                    case Shape.Folder:
                        // M3 6h6l2 2h10v11H3z
                        p.BeginPath();
                        p.MoveTo(new Vector2(3, 6) * s);
                        p.LineTo(new Vector2(9, 6) * s);
                        p.LineTo(new Vector2(11, 8) * s);
                        p.LineTo(new Vector2(21, 8) * s);
                        p.LineTo(new Vector2(21, 19) * s);
                        p.LineTo(new Vector2(3, 19) * s);
                        p.ClosePath();
                        p.Stroke();
                        break;
                    case Shape.Prev:
                    case Shape.Next:
                    {
                        // M15 5l-7 7 7 7 (previous), M9 5l7 7-7 7 (next)
                        p.lineWidth = 2.2f * s;
                        var x0 = shape == Shape.Prev ? 15f : 9f;
                        var x1 = shape == Shape.Prev ? 8f : 16f;
                        p.BeginPath();
                        p.MoveTo(new Vector2(x0, 5) * s);
                        p.LineTo(new Vector2(x1, 12) * s);
                        p.LineTo(new Vector2(x0, 19) * s);
                        p.Stroke();
                        break;
                    }
                    case Shape.Chevron:
                        // M5 9l7 7 7-7
                        p.lineWidth = 2.4f * s;
                        p.lineCap = LineCap.Butt;
                        p.lineJoin = LineJoin.Miter;
                        p.BeginPath();
                        p.MoveTo(new Vector2(5, 9) * s);
                        p.LineTo(new Vector2(12, 16) * s);
                        p.LineTo(new Vector2(19, 9) * s);
                        p.Stroke();
                        break;
                    case Shape.Path:
                        // Lucide's paths, each subpath stroked as a polyline (its curves in short segments); a subpath
                        // too short to stroke (a gamepad's button: a 0.01 line) is a dot of the line's width
                        foreach (var d in paths ?? Array.Empty<string>())
                        foreach (var line in PathPolylines(d))
                        {
                            if (line.Points.Count < 2) continue;
                            if (line.Length < 0.1f)
                            {
                                p.BeginPath();
                                p.Arc(line.Points[0] * s, p.lineWidth / 2f, Angle.Degrees(0), Angle.Degrees(360));
                                p.Fill();
                                continue;
                            }
                            p.BeginPath();
                            p.MoveTo(line.Points[0] * s);
                            for (var k = 1; k < line.Points.Count; ++k) p.LineTo(line.Points[k] * s);
                            if (line.Closed) p.ClosePath();
                            p.Stroke();
                        }
                        break;
                }
            }
        }

        /// <summary>One subpath of an SVG path as points (PathPolylines).</summary>
        internal sealed class Polyline
        {
            public readonly List<Vector2> Points = new List<Vector2>();
            public bool Closed;

            public float Length
            {
                get
                {
                    var n = 0f;
                    for (var i = 1; i < Points.Count; ++i) n += Vector2.Distance(Points[i - 1], Points[i]);
                    return n;
                }
            }
        }

        /// <summary>An SVG path's subpaths as polylines: M/L/H/V/C/S/Q/A/Z, absolute or relative (what Lucide's icons use);
        /// each curve and arc in 8 segments.</summary>
        internal static List<Polyline> PathPolylines(string d)
        {
            var lines = new List<Polyline>();
            var tokens = System.Text.RegularExpressions.Regex.Matches(d, @"[MmLlHhVvCcSsQqAaZz]|-?(?:\d+\.\d*|\.\d+|\d+)");
            var at = Vector2.zero;
            var start = Vector2.zero;
            var control = Vector2.zero;  // the last cubic's second control point (S reflects it)
            var lastCubic = false;
            Polyline cur = null;
            var cmd = 'M';
            var i = 0;
            float Num() => float.Parse(tokens[i++].Value, System.Globalization.CultureInfo.InvariantCulture);
            Vector2 Pt() => new Vector2(Num(), Num());
            void Add(Vector2 q)
            {
                if (cur == null)
                {
                    cur = new Polyline();
                    cur.Points.Add(at);
                    lines.Add(cur);
                }
                cur.Points.Add(q);
                at = q;
            }
            void Cubic(Vector2 c1, Vector2 c2, Vector2 to)
            {
                var from = at;
                for (var k = 1; k <= 8; ++k)
                {
                    var t = k / 8f;
                    var u = 1 - t;
                    Add(u * u * u * from + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * to);
                }
                control = c2;
            }
            while (i < tokens.Count)
            {
                var t = tokens[i].Value;
                if (char.IsLetter(t[0]))
                {
                    cmd = t[0];
                    ++i;
                    if (cmd == 'Z' || cmd == 'z')
                    {
                        if (cur != null) cur.Closed = true;
                        cur = null;
                        at = start;
                        lastCubic = false;
                        continue;
                    }
                }
                var rel = char.IsLower(cmd);
                var wasCubic = false;
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        var q = Pt();
                        at = rel ? at + q : q;
                        start = at;
                        cur = null;
                        cmd = rel ? 'l' : 'L';  // (more pairs after a move are lines)
                        break;
                    }
                    case 'L':
                    {
                        var q = Pt();
                        Add(rel ? at + q : q);
                        break;
                    }
                    case 'H':
                    {
                        var x = Num();
                        Add(new Vector2(rel ? at.x + x : x, at.y));
                        break;
                    }
                    case 'V':
                    {
                        var y = Num();
                        Add(new Vector2(at.x, rel ? at.y + y : y));
                        break;
                    }
                    case 'C':
                    {
                        var o = rel ? at : Vector2.zero;
                        var c1 = o + Pt();
                        var c2 = o + Pt();
                        var to = o + Pt();
                        Cubic(c1, c2, to);
                        wasCubic = true;
                        break;
                    }
                    case 'S':
                    {
                        var o = rel ? at : Vector2.zero;
                        var c1 = lastCubic ? at + (at - control) : at;
                        var c2 = o + Pt();
                        var to = o + Pt();
                        Cubic(c1, c2, to);
                        wasCubic = true;
                        break;
                    }
                    case 'Q':
                    {
                        var o = rel ? at : Vector2.zero;
                        var c = o + Pt();
                        var to = o + Pt();
                        // (a quadratic as the cubic it equals)
                        Cubic(at + 2f / 3f * (c - at), to + 2f / 3f * (c - to), to);
                        break;
                    }
                    case 'A':
                    {
                        var r = Num();
                        Num();  // (ry: the icons' arcs are circular)
                        Num();  // (rotation)
                        var large = Num() != 0;
                        var sweep = Num() != 0;
                        var q = Pt();
                        var to = rel ? at + q : q;
                        var arc = new List<Vector2>();
                        AddArc(arc, at, to, r, large, sweep);
                        foreach (var a in arc) Add(a);
                        at = to;
                        break;
                    }
                    default:
                        ++i;
                        break;
                }
                lastCubic = wasCubic;
            }
            return lines;
        }

        /// <summary>The design's gear outline (a 24 x 24 box: Lucide's settings icon, as on the boards).</summary>
        const string GearPath = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z";

        static List<Vector2> gearOutline;

        /// <summary>GearPath as points (its arcs in short segments), read once.</summary>
        internal static List<Vector2> GearOutline => gearOutline ?? (gearOutline = PathPoints(GearPath));

        /// <summary>An SVG path's points: M/L/H/V/A/Z, absolute or relative (enough for the design's icons); each arc
        /// as 8 segments.</summary>
        internal static List<Vector2> PathPoints(string d)
        {
            var points = new List<Vector2>();
            var tokens = System.Text.RegularExpressions.Regex.Matches(d, @"[MmLlHhVvAaZz]|-?(?:\d+\.\d*|\.\d+|\d+)");
            var at = Vector2.zero;
            var start = Vector2.zero;
            var cmd = 'M';
            var i = 0;
            float Num() => float.Parse(tokens[i++].Value, System.Globalization.CultureInfo.InvariantCulture);
            while (i < tokens.Count)
            {
                var t = tokens[i].Value;
                if (char.IsLetter(t[0]))
                {
                    cmd = t[0];
                    ++i;
                    if (cmd == 'Z' || cmd == 'z')
                    {
                        at = start;
                        continue;
                    }
                }
                var rel = char.IsLower(cmd);
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        var p = new Vector2(Num(), Num());
                        at = rel ? at + p : p;
                        start = at;
                        points.Add(at);
                        cmd = rel ? 'l' : 'L';  // (more pairs after a move are lines)
                        break;
                    }
                    case 'L':
                    {
                        var p = new Vector2(Num(), Num());
                        at = rel ? at + p : p;
                        points.Add(at);
                        break;
                    }
                    case 'H':
                    {
                        var x = Num();
                        at = new Vector2(rel ? at.x + x : x, at.y);
                        points.Add(at);
                        break;
                    }
                    case 'V':
                    {
                        var y = Num();
                        at = new Vector2(at.x, rel ? at.y + y : y);
                        points.Add(at);
                        break;
                    }
                    case 'A':
                    {
                        var r = Num();
                        Num();  // (ry: the design's arcs are circular)
                        Num();  // (rotation)
                        var large = Num() != 0;
                        var sweep = Num() != 0;
                        var p = new Vector2(Num(), Num());
                        var to = rel ? at + p : p;
                        AddArc(points, at, to, r, large, sweep);
                        at = to;
                        break;
                    }
                    default:
                        ++i;
                        break;
                }
            }
            return points;
        }

        /// <summary>An SVG arc (endpoint form, circular, no rotation) as points from <paramref name="from"/> to
        /// <paramref name="to"/>.</summary>
        static void AddArc(List<Vector2> points, Vector2 from, Vector2 to, float r, bool large, bool sweep)
        {
            var half = (from - to) / 2f;
            var lambda = half.sqrMagnitude / (r * r);
            if (lambda > 1) r *= Mathf.Sqrt(lambda);
            var den = half.sqrMagnitude;
            var num = Mathf.Max(0, r * r * r * r - r * r * half.y * half.y - r * r * half.x * half.x);
            var coef = (large == sweep ? -1 : 1) * (den > 0 ? Mathf.Sqrt(num / (r * r * den)) : 0);
            var cp = new Vector2(coef * half.y, -coef * half.x);
            var c = cp + (from + to) / 2f;
            var a0 = Mathf.Atan2((half.y - cp.y) / r, (half.x - cp.x) / r);
            var a1 = Mathf.Atan2((-half.y - cp.y) / r, (-half.x - cp.x) / r);
            var da = a1 - a0;
            if (!sweep && da > 0) da -= 2 * Mathf.PI;
            else if (sweep && da < 0) da += 2 * Mathf.PI;
            const int steps = 8;
            for (var k = 1; k <= steps; ++k)
            {
                var a = a0 + da * k / steps;
                points.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
        }

        static Texture2D iconTexture;

        /// <summary>The same icon rasterised for the window's tab (and the toolbar).</summary>
        public static Texture2D Icon
        {
            get
            {
                if (iconTexture != null) return iconTexture;
                const int n = 32;
                var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
                var pixels = new Color32[n * n];
                var scale = n / 24f;
                for (var y = 0; y < n; ++y)
                for (var x = 0; x < n; ++x)
                {
                    // texture rows go up; the icon's go down
                    var q = new Vector2((x + 0.5f) / scale, (n - 1 - y + 0.5f) / scale);
                    var cover = 0f;
                    for (var i = 0; i < Body.Length; ++i)
                        cover = Mathf.Max(cover, Coverage(Segment(q, Body[i], Body[(i + 1) % Body.Length]), Stroke / 2f, scale));
                    for (var i = 0; i < 2; ++i)
                        cover = Mathf.Max(cover, Coverage(Segment(q, Pad[i, 0], Pad[i, 1]), Stroke / 2f, scale));
                    foreach (var b in Buttons)
                        cover = Mathf.Max(cover, Coverage(Vector2.Distance(q, b), ButtonRadius, scale));
                    var c = (Color32)Accent;
                    c.a = (byte)Mathf.RoundToInt(255 * cover);
                    pixels[y * n + x] = c;
                }
                t.SetPixels32(pixels);
                t.Apply();
                iconTexture = t;
                return t;
            }
        }

        static float Coverage(float distance, float radius, float scale)
        {
            return Mathf.Clamp01((radius - distance) * scale + 0.5f);
        }

        static float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + t * ab);
        }

        // -- drawn pieces ------------------------------------------------------------------------------------

        /// <summary>Something going (installing, starting, waiting for the browser): a turning square, 12 px, its
        /// sides #3a5578 and its top #7aa5d8.</summary>
        public sealed class Spinner : VisualElement
        {
            float angle;

            public Spinner(float size = 12)
            {
                AddToClassList("spinner");
                style.width = size;
                style.height = size;
                style.flexShrink = 0;
                style.marginRight = 10;
                style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 2;
                style.borderBottomColor = style.borderLeftColor = style.borderRightColor = InfoLine;
                style.borderTopColor = Info;
                schedule.Execute(() =>
                {
                    angle = (angle + 30) % 360;
                    style.rotate = new Rotate(angle);
                }).Every(80);
            }
        }

        /// <summary>A success tick (M4 12l5 5L20 6, #74b078): done, ready.</summary>
        public sealed class Check : VisualElement
        {
            public Check(float size = 18)
            {
                AddToClassList("check");
                style.width = size;
                style.height = size;
                style.flexShrink = 0;
                style.marginRight = 10;
                generateVisualContent += Draw;
            }

            void Draw(MeshGenerationContext mgc)
            {
                var s = contentRect.width / 24f;
                var p = mgc.painter2D;
                p.strokeColor = Success;
                p.lineWidth = 2.4f * s;
                p.lineCap = LineCap.Butt;
                p.lineJoin = LineJoin.Miter;
                p.BeginPath();
                p.MoveTo(new Vector2(4, 12) * s);
                p.LineTo(new Vector2(9, 17) * s);
                p.LineTo(new Vector2(20, 6) * s);
                p.Stroke();
            }
        }

        /// <summary>A small square dot (the agent service's state in a line: 7 px in the setup, 6 on the Settings page).</summary>
        public static VisualElement Dot(Color color, float size = 7)
        {
            var d = new VisualElement();
            d.style.width = size;
            d.style.height = size;
            d.style.flexShrink = 0;
            d.style.marginRight = size >= 7 ? 10 : 8;
            d.style.backgroundColor = color;
            return d;
        }

        /// <summary>The verdict: the word in capitals, ink on a solid block of its colour.</summary>
        public static VisualElement Verdict(string word, Color color)
        {
            var e = Box("verdict");
            e.style.backgroundColor = color;
            e.Add(L(word.ToUpperInvariant()));
            return e;
        }

        /// <summary>A rounded rectangle's path (the radius at most half the shorter side: a pill).</summary>
        public static void RoundedRect(Painter2D p, float x, float y, float w, float h, float radius)
        {
            var r = Mathf.Max(0, Mathf.Min(radius, Mathf.Min(w, h) / 2f));
            p.BeginPath();
            p.MoveTo(new Vector2(x + r, y));
            p.LineTo(new Vector2(x + w - r, y));
            p.ArcTo(new Vector2(x + w, y), new Vector2(x + w, y + r), r);
            p.LineTo(new Vector2(x + w, y + h - r));
            p.ArcTo(new Vector2(x + w, y + h), new Vector2(x + w - r, y + h), r);
            p.LineTo(new Vector2(x + r, y + h));
            p.ArcTo(new Vector2(x, y + h), new Vector2(x, y + h - r), r);
            p.LineTo(new Vector2(x, y + r));
            p.ArcTo(new Vector2(x, y), new Vector2(x + r, y), r);
            p.ClosePath();
        }

        /// <summary>A stacked bar: (colour, fraction) segments over a dark track.</summary>
        public sealed class Bar : VisualElement
        {
            readonly List<(Color color, float fraction)> segments = new List<(Color, float)>();

            public Bar()
            {
                generateVisualContent += Draw;
            }

            public void Set(IEnumerable<(Color color, float fraction)> parts)
            {
                segments.Clear();
                segments.AddRange(parts);
                MarkDirtyRepaint();
            }

            void Draw(MeshGenerationContext mgc)
            {
                var r = contentRect;
                if (r.width <= 0) return;
                var p = mgc.painter2D;
                FillRect(p, 0, 0, r.width, r.height, Hover);
                var x = 0f;
                foreach (var (color, fraction) in segments)
                {
                    var w = Mathf.Clamp01(fraction) * r.width;
                    if (x + w > r.width) w = r.width - x;
                    if (w <= 0) continue;
                    FillRect(p, x, 0, w, r.height, color);
                    x += w;
                }
            }
        }

        public static void FillRect(Painter2D p, float x, float y, float w, float h, Color c)
        {
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(new Vector2(x, y));
            p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h));
            p.LineTo(new Vector2(x, y + h));
            p.ClosePath();
            p.Fill();
        }

        public static void Diamond(Painter2D p, Vector2 c, float r, Color fill, Color stroke, float strokeWidth)
        {
            p.BeginPath();
            p.MoveTo(c + new Vector2(0, -r));
            p.LineTo(c + new Vector2(r, 0));
            p.LineTo(c + new Vector2(0, r));
            p.LineTo(c + new Vector2(-r, 0));
            p.ClosePath();
            if (fill.a > 0)
            {
                p.fillColor = fill;
                p.Fill();
            }
            if (stroke.a > 0 && strokeWidth > 0)
            {
                p.strokeColor = stroke;
                p.lineWidth = strokeWidth;
                p.Stroke();
            }
        }

        /// <summary>LAUNCH · ANALYZE · PROBE ENV · BUILD HARNESS · ROLLOUT · PROCESS RESULT · REPORT as a 2 px track:
        /// the accent up to the middle of the stage going (all of it once finished), the rule's colour after.</summary>
        public sealed class StageTrack : VisualElement
        {
            int current = -1;
            bool finished;

            public StageTrack()
            {
                AddToClassList("stage-track");
                generateVisualContent += Draw;
            }

            public void Set(int stage, bool done)
            {
                current = stage;
                finished = done;
                MarkDirtyRepaint();
            }

            /// <summary>How much of the track is lit: to the middle of the stage going.</summary>
            public static float Lit(int stage, bool done, int stages)
            {
                if (stages <= 0) return 0;
                if (done) return 1;
                return Mathf.Clamp01((Mathf.Max(0, stage) + 0.5f) / stages);
            }

            void Draw(MeshGenerationContext mgc)
            {
                var r = contentRect;
                if (r.width <= 0 || r.height <= 0) return;
                var p = mgc.painter2D;
                FillRect(p, 0, 0, r.width, r.height, Line);
                var w = current < 0 && !finished ? 0 : Lit(current, finished, AutopilotText.Stages.Length) * r.width;
                if (w > 0) FillRect(p, 0, 0, w, r.height, Accent);
            }
        }
    }
}
