// What is on screen: texts (screen), and the controls a player's mouse can use (ui, ui_act). uGUI (Text, TextMeshPro,
// Selectables on screen-space canvases), UI Toolkit (UIDocuments) and IMGUI (OnGUI: the game's GUI and GUILayout calls,
// noted as they draw, see Imgui.cs). World-space canvases are things in the world: view tells their texts.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if AUTOPILOT_UGUI
using UnityEngine.EventSystems;
using UnityEngine.UI;
#endif
#if AUTOPILOT_TMP
using TMPro;
#endif
#if AUTOPILOT_UITK
using UIE = UnityEngine.UIElements;
#endif

namespace Autopilot.Playtest
{
    public static class ScreenText
    {
        static string Unique(Dictionary<string, string> taken, string key)
        {
            if (!taken.ContainsKey(key)) return key;
            for (var i = 2; ; ++i)
                if (!taken.ContainsKey(key + "#" + i))
                    return key + "#" + i;
        }

#if AUTOPILOT_UGUI
        /// <summary>Whether a UI object is drawn on screen: active, on an enabled screen-space canvas, not faded out.</summary>
        public static bool OnScreen(Component c, out Canvas root)
        {
            root = null;
            if (c == null || !c.gameObject.activeInHierarchy) return false;
            if (c is Behaviour b && !b.enabled) return false;
            var canvas = c.GetComponentInParent<Canvas>();
            if (canvas == null || !canvas.isActiveAndEnabled) return false;
            root = canvas.rootCanvas;
            if (root.renderMode == RenderMode.WorldSpace) return false;
            foreach (var g in c.GetComponentsInParent<CanvasGroup>())
            {
                if (g.alpha <= 0.001f) return false;
                if (g.ignoreParentGroups) break;
            }
            return true;
        }

        public static string Owner(Canvas root)
        {
            return root != null ? root.name : "";
        }
#endif

        /// <summary>screen: {key: text} of every visible text on screen.</summary>
        public static Dictionary<string, string> Texts()
        {
            var texts = new Dictionary<string, string>();
#if AUTOPILOT_UGUI
            foreach (var t in Loaded.All<Text>(FindObjectsInactive.Exclude))
            {
                if (!OnScreen(t, out var root) || string.IsNullOrWhiteSpace(t.text) || t.color.a <= 0.001f) continue;
                texts[Unique(texts, Owner(root) + "." + t.gameObject.name)] = t.text.Trim();
            }
#endif
#if AUTOPILOT_TMP
            foreach (var t in Loaded.All<TMP_Text>(FindObjectsInactive.Exclude))
            {
                if (t is TextMeshPro) continue;  // (a text in the world: view tells it)
                if (!OnScreen(t, out var root) || string.IsNullOrWhiteSpace(t.text) || t.color.a <= 0.001f || t.alpha <= 0.001f) continue;
                texts[Unique(texts, Owner(root) + "." + t.gameObject.name)] = Strip(t.text).Trim();
            }
#endif
#if AUTOPILOT_UITK
            foreach (var doc in Loaded.All<UIE.UIDocument>(FindObjectsInactive.Exclude))
            {
                if (doc == null || !doc.isActiveAndEnabled || doc.rootVisualElement == null) continue;
                UIE.UQueryExtensions.Query<UIE.TextElement>(doc.rootVisualElement).ForEach(e =>
                {
                    if (!Visible(e) || string.IsNullOrWhiteSpace(e.text)) return;
                    var name = !string.IsNullOrEmpty(e.name) ? e.name : e.GetType().Name;
                    texts[Unique(texts, doc.gameObject.name + "." + name)] = e.text.Trim();
                });
            }
#endif
#if AUTOPILOT_IMGUI
            Imgui.AddTexts(texts);
#endif
            return texts;
        }

        static string Strip(string rich)
        {
            // TextMeshPro's rich-text tags (<b>, <color=...>) aren't what a player reads
            return System.Text.RegularExpressions.Regex.Replace(rich, "<[^<>]{1,40}>", "");
        }

#if AUTOPILOT_UITK
        public static bool Visible(UIE.VisualElement e)
        {
            if (e.panel == null) return false;
            for (var v = e; v != null; v = v.parent)
            {
                var s = v.resolvedStyle;
                if (s.display == UIE.DisplayStyle.None || s.visibility == UIE.Visibility.Hidden || s.opacity <= 0.001f) return false;
            }
            return true;
        }
#endif
    }

    public static class ScreenUI
    {
        sealed class Element
        {
            public string Id, Kind, Name, Owner, Label;
            public bool Enabled;
            public object Target;  // a uGUI Selectable, or a UI Toolkit element
        }

        static List<Element> last = new List<Element>();

        static string UniqueId(HashSet<string> ids, string id)
        {
            var u = id;
            for (var i = 2; ids.Contains(u); ++i) u = id + "#" + i;
            ids.Add(u);
            return u;
        }

        static List<Element> Scan()
        {
            var list = new List<Element>();
            var ids = new HashSet<string>();
#if AUTOPILOT_UGUI
            var selectables = Loaded.All<Selectable>(FindObjectsInactive.Exclude)
                .OrderBy(s => s.transform.root.GetSiblingIndex()).ThenBy(s => Depth(s.transform)).ToList();
            foreach (var s in selectables)
            {
                if (!ScreenText.OnScreen(s, out var root)) continue;
                var kind = Kind(s);
                if (kind == null) continue;
                var owner = ScreenText.Owner(root);
                list.Add(new Element
                {
                    Id = UniqueId(ids, owner + "." + s.gameObject.name),
                    Kind = kind,
                    Name = s.gameObject.name,
                    Owner = owner,
                    Label = Label(s.gameObject),
                    Enabled = s.IsInteractable(),
                    Target = s,
                });
            }
#endif
#if AUTOPILOT_UITK
            foreach (var doc in Loaded.All<UIE.UIDocument>(FindObjectsInactive.Exclude))
            {
                if (doc == null || !doc.isActiveAndEnabled || doc.rootVisualElement == null) continue;
                UIE.UQueryExtensions.Query<UIE.VisualElement>(doc.rootVisualElement).ForEach(e =>
                {
                    var kind = e is UIE.Button ? "button" : e is UIE.Toggle ? "checkbox" : e is UIE.DropdownField ? "combo"
                        : e is UIE.Slider || e is UIE.SliderInt ? "slider" : e is UIE.TextField ? "text_input" : null;
                    if (kind == null || !ScreenText.Visible(e)) return;
                    var name = !string.IsNullOrEmpty(e.name) ? e.name : e.GetType().Name;
                    var label = e is UIE.TextElement te ? te.text : e is UIE.BaseField<bool> bf ? bf.label : e is UIE.DropdownField df ? df.label
                        : e is UIE.TextField tf ? tf.label : e is UIE.Slider sl ? sl.label : "";
                    list.Add(new Element
                    {
                        Id = UniqueId(ids, doc.gameObject.name + "." + name),
                        Kind = kind,
                        Name = name,
                        Owner = doc.gameObject.name,
                        Label = label ?? "",
                        Enabled = e.enabledInHierarchy,
                        Target = e,
                    });
                });
            }
#endif
            return list;
        }

        static int Depth(Transform t)
        {
            var d = 0;
            for (; t != null; t = t.parent) ++d;
            return d;
        }

#if AUTOPILOT_UGUI
        static string Kind(Selectable s)
        {
            switch (s)
            {
                case Button _: return "button";
                case Toggle _: return "checkbox";
                case Slider _: return "slider";
                case Scrollbar _: return "slider";
                case Dropdown _: return "combo";
                case InputField _: return "text_input";
            }
#if AUTOPILOT_TMP
            if (s is TMP_Dropdown) return "combo";
            if (s is TMP_InputField) return "text_input";
#endif
            return "widget";
        }

        /// <summary>The visible texts inside, then the names of its images' sprites (an icon says what a slot holds).</summary>
        static string Label(GameObject go)
        {
            var found = new List<string>();
            foreach (var t in go.GetComponentsInChildren<Text>())
                if (t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text) && found.Count < 4 && !found.Contains(t.text.Trim()))
                    found.Add(t.text.Trim());
#if AUTOPILOT_TMP
            foreach (var t in go.GetComponentsInChildren<TMP_Text>())
                if (t.isActiveAndEnabled && !string.IsNullOrWhiteSpace(t.text) && found.Count < 4 && !found.Contains(t.text.Trim()))
                    found.Add(t.text.Trim());
#endif
            var icons = 0;
            foreach (var img in go.GetComponentsInChildren<Image>())
            {
                if (!img.isActiveAndEnabled || img.sprite == null || icons >= 2) continue;
                if (img.sprite.name == "UISprite" || img.sprite.name == "Background" || img.sprite.name == "Knob" || img.sprite.name == "Checkmark" || img.sprite.name == "UIMask" || img.sprite.name == "DropdownArrow" || img.sprite.name == "InputFieldBackground") continue;
                found.Add("[" + img.sprite.name + "]");
                ++icons;
            }
            return string.Join(" / ", found);
        }
#endif

        /// <summary>ui: the controls on screen ({"id", "kind", "name", "owner", "label", "enabled", ...}).</summary>
        public static List<object> List()
        {
            last = Scan();
            var outList = new List<object>();
            foreach (var e in last)
            {
                var item = new Dictionary<string, object>
                {
                    ["id"] = e.Id, ["kind"] = e.Kind, ["name"] = e.Name, ["owner"] = e.Owner, ["label"] = e.Label, ["enabled"] = e.Enabled,
                };
#if AUTOPILOT_UGUI
                switch (e.Target)
                {
                    case Toggle t: item["checked"] = t.isOn; break;
                    case Slider s:
                        item["value"] = Json.Round(s.value, 4);
                        item["min"] = Json.Round(s.minValue, 4);
                        item["max"] = Json.Round(s.maxValue, 4);
                        break;
                    case Scrollbar sb:
                        item["value"] = Json.Round(sb.value, 4);
                        item["min"] = 0.0;
                        item["max"] = 1.0;
                        break;
                    case Dropdown d:
                        item["selected"] = d.options.Count > d.value && d.value >= 0 ? d.options[d.value].text : "";
                        item["options"] = d.options.Take(20).Select(o => (object)o.text).ToList();
                        break;
                    case InputField f:
                        item["text"] = f.text;
                        item["placeholder"] = f.placeholder is Text ph ? ph.text : "";
                        break;
                }
#if AUTOPILOT_TMP
                switch (e.Target)
                {
                    case TMP_Dropdown d:
                        item["selected"] = d.options.Count > d.value && d.value >= 0 ? d.options[d.value].text : "";
                        item["options"] = d.options.Take(20).Select(o => (object)o.text).ToList();
                        break;
                    case TMP_InputField f:
                        item["text"] = f.text;
                        item["placeholder"] = f.placeholder is TMP_Text tph ? tph.text : f.placeholder is Text uph ? uph.text : "";
                        break;
                }
#endif
#endif
#if AUTOPILOT_UITK
                switch (e.Target)
                {
                    case UIE.Toggle t: item["checked"] = t.value; break;
                    case UIE.Slider s:
                        item["value"] = Json.Round(s.value, 4);
                        item["min"] = Json.Round(s.lowValue, 4);
                        item["max"] = Json.Round(s.highValue, 4);
                        break;
                    case UIE.SliderInt si:
                        item["value"] = si.value;
                        item["min"] = si.lowValue;
                        item["max"] = si.highValue;
                        break;
                    case UIE.DropdownField d:
                        item["selected"] = d.value ?? "";
                        item["options"] = d.choices.Take(20).Select(o => (object)o).ToList();
                        break;
                    case UIE.TextField f:
                        item["text"] = f.value;
#if UNITY_6000_0_OR_NEWER
                        item["placeholder"] = f.textEdition.placeholder ?? "";
#else
                        item["placeholder"] = "";  // UI Toolkit in 2022.3 has no native TextField placeholder.
#endif
                        break;
                }
#endif
                outList.Add(item);
            }
#if AUTOPILOT_IMGUI
            var taken = new HashSet<string>(last.Select(e => e.Id));
            foreach (var item in Imgui.Items())
                if (taken.Add((string)item["id"]))
                    outList.Add(item);
#endif
            return outList;
        }

        /// <summary>ui_act: {"id", "action": "click"|"right_click"|"double_click"|"set"|"drag", "value"?, "text"?, "option"?, "to"?}.
        /// Returns "" or what went wrong.</summary>
        public static string Act(Dictionary<string, object> args)
        {
            var id = Json.Str(args, "id");
            var action = Json.Str(args, "action", "click");
            last = Scan();
            var e = last.FirstOrDefault(x => x.Id == id);
#if AUTOPILOT_IMGUI
            if (e == null)
            {
                var drawn = Imgui.Act(id, action, args);
                if (drawn != null) return drawn;
            }
#endif
            if (e == null) return $"no element {id} on screen";
            if (!e.Enabled) return $"{id} is disabled";
#if AUTOPILOT_UGUI
            if (e.Target is Selectable s)
            {
                var go = s.gameObject;
                switch (action)
                {
                    case "click":
                        if (s is Dropdown dd && Json.Has(args, "option"))
                            return SetDropdown(id, Json.Str(args, "option"), dd.options.Select(o => o.text).ToList(), i => dd.value = i);
#if AUTOPILOT_TMP
                        if (s is TMP_Dropdown td && Json.Has(args, "option"))
                            return SetDropdown(id, Json.Str(args, "option"), td.options.Select(o => o.text).ToList(), i => td.value = i);
#endif
                        Click(go, PointerEventData.InputButton.Left, 1);
                        return "";
                    case "right_click":
                        Click(go, PointerEventData.InputButton.Right, 1);
                        return "";
                    case "double_click":
                        Click(go, PointerEventData.InputButton.Left, 1);
                        Click(go, PointerEventData.InputButton.Left, 2);
                        return "";
                    case "set":
                        switch (s)
                        {
                            case Slider sl:
                                sl.value = (float)Json.Num(args, "value", sl.value);
                                return "";
                            case Scrollbar sb:
                                sb.value = (float)Json.Num(args, "value", sb.value);
                                return "";
                            case Toggle t:
                                t.isOn = Json.Bool(args, "value", !t.isOn);
                                return "";
                            case Dropdown d:
                                return SetDropdown(id, Json.Str(args, "option", Json.Str(args, "text")), d.options.Select(o => o.text).ToList(), i => d.value = i);
                            case InputField f:
                                f.text = Json.Str(args, "text");
                                f.onEndEdit.Invoke(f.text);
                                f.onSubmit?.Invoke(f.text);
                                return "";
                        }
#if AUTOPILOT_TMP
                        if (s is TMP_InputField tf)
                        {
                            tf.text = Json.Str(args, "text");
                            tf.onEndEdit.Invoke(tf.text);
                            tf.onSubmit.Invoke(tf.text);
                            return "";
                        }
                        if (s is TMP_Dropdown tdd)
                            return SetDropdown(id, Json.Str(args, "option", Json.Str(args, "text")), tdd.options.Select(o => o.text).ToList(), i => tdd.value = i);
#endif
                        return $"{id} takes no value";
                    case "drag":
                    {
                        var to = last.FirstOrDefault(x => x.Id == Json.Str(args, "to"));
                        if (to == null || !(to.Target is Component target)) return $"no element {Json.Str(args, "to")} on screen to drop on";
                        var data = Pointer(go, PointerEventData.InputButton.Left, 1);
                        ExecuteEvents.Execute(go, data, ExecuteEvents.pointerDownHandler);
                        ExecuteEvents.Execute(go, data, ExecuteEvents.initializePotentialDrag);
                        data.dragging = true;
                        ExecuteEvents.Execute(go, data, ExecuteEvents.beginDragHandler);
                        ExecuteEvents.Execute(go, data, ExecuteEvents.dragHandler);
                        data.pointerEnter = target.gameObject;
                        var dropped = ExecuteEvents.ExecuteHierarchy(target.gameObject, data, ExecuteEvents.dropHandler) != null;
                        ExecuteEvents.Execute(go, data, ExecuteEvents.endDragHandler);
                        ExecuteEvents.Execute(go, data, ExecuteEvents.pointerUpHandler);
                        return dropped ? "" : $"{to.Id} didn't take the drop";
                    }
                }
                return $"unknown action {action}";
            }
#endif
#if AUTOPILOT_UITK
            if (e.Target is UIE.VisualElement ve)
            {
                switch (action)
                {
                    case "click":
                    case "double_click":
                        if (ve is UIE.Toggle t)
                        {
                            t.value = !t.value;
                            return "";
                        }
                        if (ve is UIE.DropdownField df && Json.Has(args, "option"))
                        {
                            var option = Json.Str(args, "option");
                            if (!df.choices.Contains(option)) return $"{id} has no option {option}";
                            df.value = option;
                            return "";
                        }
                        using (var submit = UIE.NavigationSubmitEvent.GetPooled())
                        {
                            submit.target = ve;
                            ve.SendEvent(submit);
                        }
                        using (var click = UIE.ClickEvent.GetPooled())
                        {
                            click.target = ve;
                            ve.SendEvent(click);
                        }
                        return "";
                    case "right_click":
                        // (as a player's right button: down and up over it, then its context click)
                        using (var down = UIE.PointerDownEvent.GetPooled(Pointer(ve, 1)))
                        {
                            down.target = ve;
                            ve.SendEvent(down);
                        }
                        using (var up = UIE.PointerUpEvent.GetPooled(Pointer(ve, 1)))
                        {
                            up.target = ve;
                            ve.SendEvent(up);
                        }
                        using (var context = UIE.ContextClickEvent.GetPooled(Pointer(ve, 1)))
                        {
                            context.target = ve;
                            ve.SendEvent(context);
                        }
                        return "";
                    case "drag":
                    {
                        // Pressed over it, moved over the element dropped on, let go there (the pointer events a UI Toolkit
                        // drag-and-drop manipulator reads)
                        var to = last.FirstOrDefault(x => x.Id == Json.Str(args, "to"));
                        if (to == null || !(to.Target is UIE.VisualElement target)) return $"no element {Json.Str(args, "to")} on screen to drop on";
                        var from = Pointer(ve, 0);
                        var over = Pointer(target, 0);
                        using (var down = UIE.PointerDownEvent.GetPooled(from))
                        {
                            down.target = ve;
                            ve.SendEvent(down);
                        }
                        using (var move = UIE.PointerMoveEvent.GetPooled(over))
                        {
                            move.target = ve;
                            ve.SendEvent(move);
                        }
                        using (var move = UIE.PointerMoveEvent.GetPooled(over))
                        {
                            move.target = target;
                            target.SendEvent(move);
                        }
                        using (var up = UIE.PointerUpEvent.GetPooled(over))
                        {
                            up.target = target;
                            target.SendEvent(up);
                        }
                        return "";
                    }
                    case "set":
                        switch (ve)
                        {
                            case UIE.Slider sl: sl.value = (float)Json.Num(args, "value", sl.value); return "";
                            case UIE.SliderInt si: si.value = (int)Json.Num(args, "value", si.value); return "";
                            case UIE.Toggle tg: tg.value = Json.Bool(args, "value", !tg.value); return "";
                            case UIE.TextField tf: tf.value = Json.Str(args, "text"); return "";
                            case UIE.DropdownField d:
                                var opt = Json.Str(args, "option", Json.Str(args, "text"));
                                if (!d.choices.Contains(opt)) return $"{id} has no option {opt}";
                                d.value = opt;
                                return "";
                        }
                        return $"{id} takes no value";
                }
                return $"unknown action {action} for a UI Toolkit element";
            }
#endif
            return $"no element {id} on screen";
        }

#if AUTOPILOT_UITK
        /// <summary>A mouse event at an element's middle with a button (0 left, 1 right) down, for the pointer events.</summary>
        static Event Pointer(UIE.VisualElement e, int button)
        {
            var at = e.worldBound.center;
            return new Event { type = EventType.MouseDown, button = button, mousePosition = at, clickCount = 1 };
        }
#endif

        static string SetDropdown(string id, string option, List<string> options, Action<int> set)
        {
            var i = options.FindIndex(o => o == option);
            if (i < 0) i = options.FindIndex(o => string.Equals(o, option, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return $"{id} has no option {option} (it has: {string.Join(", ", options.Take(20))})";
            set(i);
            return "";
        }

#if AUTOPILOT_UGUI
        static PointerEventData Pointer(GameObject go, PointerEventData.InputButton button, int clicks)
        {
            var data = new PointerEventData(EventSystem.current)
            {
                button = button,
                clickCount = clicks,
                clickTime = Time.unscaledTime,
                pointerPress = go,
                rawPointerPress = go,
                pointerEnter = go,
                eligibleForClick = true,
            };
            if (go.transform is RectTransform rt)
            {
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                data.position = (corners[0] + corners[2]) / 2f;
                data.pressPosition = data.position;
            }
            return data;
        }

        /// <summary>As a player's mouse: over it, pressed, released, clicked (the events the input module sends).</summary>
        static void Click(GameObject go, PointerEventData.InputButton button, int clicks)
        {
            var data = Pointer(go, button, clicks);
            ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerEnterHandler);
            var pressed = ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerDownHandler) ?? go;
            data.pointerPress = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go) ?? pressed;
            ExecuteEvents.Execute(pressed, data, ExecuteEvents.pointerUpHandler);
            if (data.pointerPress != null)
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerClickHandler);
            ExecuteEvents.ExecuteHierarchy(go, data, ExecuteEvents.pointerExitHandler);
        }
#endif
    }
}
